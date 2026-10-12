using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PKGMerge.Core;

internal sealed record Segment(
    PackagePart Part,
    int PartPosition,
    string Path,
    long Offset,
    long Length,
    bool SharedWithOutput = false)
{
    public long End => Offset + Length;
}

internal sealed class MergePipeline
{
    private readonly record struct Block(AlignedBuffer Buffer, int Length, long OutputOffset);

    private readonly SafeFileHandle _output;
    private readonly long _outputStart;
    private readonly IReadOnlyList<Segment> _segments;
    private readonly long[] _segmentEnds;
    private readonly long _totalSize;
    private readonly int _partCount;
    private readonly int _bufferSize;
    private readonly int _bufferCount;
    private readonly int _alignment;
    private readonly IProgress<MergeProgress>? _progress;
    private readonly Action<Segment>? _segmentWritten;

    private int _progressSegment;
    private int _writtenSegments;

    public MergePipeline(
        SafeFileHandle output,
        long outputStart,
        IReadOnlyList<Segment> segments,
        int partCount,
        int bufferSize,
        int bufferCount,
        int alignment,
        IProgress<MergeProgress>? progress,
        Action<Segment>? segmentWritten = null)
    {
        _output = output;
        _outputStart = outputStart;
        _segments = segments;
        _partCount = partCount;
        _bufferSize = bufferSize;
        _bufferCount = bufferCount;
        _alignment = alignment;
        _progress = progress;
        _segmentWritten = segmentWritten;

        _segmentEnds = new long[segments.Count];
        long end = outputStart;
        for (int i = 0; i < segments.Count; i++)
            _segmentEnds[i] = end += segments[i].Length;

        _totalSize = end - outputStart;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        List<AlignedBuffer> buffers = [];
        SafeFileHandle?[] inputs = new SafeFileHandle?[_segments.Count];
        try
        {
            // Open everything up front: a locked or changed file fails the merge before anything is written.
            for (int i = 0; i < _segments.Count; i++)
            {
                inputs[i] = OpenInput(_segments[i]);
                CheckLength(inputs[i]!, _segments[i]);
            }

            Channel<AlignedBuffer> free = Channel.CreateUnbounded<AlignedBuffer>(new UnboundedChannelOptions { SingleReader = true });
            Channel<Block> full = Channel.CreateUnbounded<Block>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

            for (int i = 0; i < _bufferCount; i++)
            {
                AlignedBuffer buffer = new(_bufferSize, Math.Max(_alignment, MemoryAlignment));
                buffers.Add(buffer);
                free.Writer.TryWrite(buffer);
            }

            ReportProgress(_outputStart);

            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task reader = Task.Run(() => StopOnFailure(ReadAsync(inputs, free.Reader, full.Writer, stop.Token), stop), CancellationToken.None);
            Task writer = Task.Run(() => StopOnFailure(WriteAsync(full.Reader, free.Writer, stop.Token), stop), CancellationToken.None);

            try
            {
                await Task.WhenAll(reader, writer).ConfigureAwait(false);
            }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The side that failed first cancels the other one, report the original error
                Exception? error = new[] { reader, writer }
                    .Select(t => t.Exception?.InnerException)
                    .FirstOrDefault(e => e is not null and not OperationCanceledException);

                if (error is not null)
                    ExceptionDispatchInfo.Throw(error);

                throw;
            }
        }
        finally
        {
            foreach (AlignedBuffer buffer in buffers)
                ((IDisposable)buffer).Dispose();

            foreach (SafeFileHandle? input in inputs)
                input?.Dispose();
        }
    }

    private static SafeFileHandle OpenInput(Segment segment) => File.OpenHandle(
        segment.Path,
        FileMode.Open,
        FileAccess.Read,
        segment.SharedWithOutput ? FileShare.ReadWrite : FileShare.Read | FileShare.Delete,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public const int MemoryAlignment = 4096;

    private static async Task StopOnFailure(Task task, CancellationTokenSource stop)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            stop.Cancel();
            throw;
        }
    }

    private async Task ReadAsync(
        SafeFileHandle?[] inputs, ChannelReader<AlignedBuffer> free, ChannelWriter<Block> full, CancellationToken cancellationToken)
    {
        try
        {
            AlignedBuffer? buffer = null;
            int filled = 0;
            long blockOffset = _outputStart;

            for (int i = 0; i < _segments.Count; i++)
            {
                Segment segment = _segments[i];
                SafeFileHandle input = inputs[i]!;

                long position = segment.Offset;
                while (position < segment.End)
                {
                    if (buffer is null)
                    {
                        buffer = await free.ReadAsync(cancellationToken).ConfigureAwait(false);
                        filled = 0;
                    }

                    int count = (int)Math.Min(_bufferSize - filled, segment.End - position);
                    int read = await RandomAccess.ReadAsync(input, buffer.Memory.Slice(filled, count), position, cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0)
                        throw Changed(segment.Part, RandomAccess.GetLength(input));

                    filled += read;
                    position += read;

                    // Check and close before the data is handed over: an in-place merge deletes the piece once it's
                    // written and a deleted file's space is only freed once no handle to it is open
                    if (position == segment.End)
                    {
                        CheckLength(input, segment);
                        input.Dispose();
                    }

                    if (filled == _bufferSize)
                    {
                        await full.WriteAsync(new Block(buffer, filled, blockOffset), cancellationToken).ConfigureAwait(false);
                        blockOffset += filled;
                        buffer = null;
                    }
                }
            }

            if (buffer is not null && filled > 0)
                await full.WriteAsync(new Block(buffer, filled, blockOffset), cancellationToken).ConfigureAwait(false);

            full.Complete();
        }
        catch (Exception ex)
        {
            full.TryComplete(ex);
            throw;
        }
    }

    private async Task WriteAsync(ChannelReader<Block> full, ChannelWriter<AlignedBuffer> free, CancellationToken cancellationToken)
    {
        await foreach (Block block in full.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Unbuffered writes must cover whole sectors: pad the last block, the file is cut to size afterwards
            int length = (int)AlignUp(block.Length, _alignment);
            if (length != block.Length)
                block.Buffer.Memory.Span[block.Length..length].Clear();

            await RandomAccess.WriteAsync(_output, block.Buffer.Memory[..length], block.OutputOffset, cancellationToken)
                .ConfigureAwait(false);

            free.TryWrite(block.Buffer);

            long written = block.OutputOffset + block.Length;
            ReportProgress(written);

            for (; _writtenSegments < _segments.Count && _segmentEnds[_writtenSegments] <= written; _writtenSegments++)
                _segmentWritten?.Invoke(_segments[_writtenSegments]);
        }
    }

    private void ReportProgress(long written)
    {
        if (_progress is null)
            return;

        while (_progressSegment < _segments.Count - 1 && written > _segmentEnds[_progressSegment])
            _progressSegment++;

        _progress.Report(new MergeProgress(_segments[_progressSegment].PartPosition, _partCount, written - _outputStart, _totalSize));
    }

    private static void CheckLength(SafeFileHandle input, Segment segment)
    {
        long length = RandomAccess.GetLength(input);
        if (length != segment.Part.Size)
            throw Changed(segment.Part, length);
    }

    internal static IOException Changed(PackagePart part, long length) =>
        new($"'{part.FileName}' changed since it was scanned (expected {part.Size} bytes, found {length}).");

    public static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    public static long AlignDown(long value, int alignment) => value / alignment * alignment;
}
