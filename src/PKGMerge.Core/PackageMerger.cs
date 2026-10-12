using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PKGMerge.Core;

public sealed class PackageMerger
{
    public const string PartialExtension = ".partial";

    // 64 MiB: reading and writing the same HDD ran at ~75 MB/s, against ~50 MB/s with 16 MiB and ~30 MB/s with 1 MiB
    // (fewer seeks between the two, head doesn't travel that much) and as fast as smaller buffers when the source 
    // is on another drive. Keep in mind this was just my testing done on single HDD, doesn't have meaningful impact on SSDs.
    public const int DefaultBufferSize = 64 * 1024 * 1024;

    // Largest buffer the CLI and the app offer, 256 MiB: larger ones didn't make merging faster.
    public const int MaxBufferSize = 256 * 1024 * 1024;

    // One buffer is filled while the other is written; more buffers didn't make a measurable difference
    private const int BufferCount = 2;

    // https://learn.microsoft.com/en-us/windows/win32/fileio/file-buffering
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public int BufferSize { get; init; } = DefaultBufferSize;

    public async Task<string> MergeAsync(
        SplitPackage package,
        string outputPath,
        IProgress<MergeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string fullOutputPath = CheckArguments(package, outputPath);
        string partialPath = fullOutputPath + PartialExtension;

        Validate(package);
        ThrowIfPartialExists(partialPath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);

        IReadOnlyList<PackagePart> parts = package.Parts;
        long totalSize = package.TotalSize;

        SafeFileHandle output = OpenOutput(partialPath, FileMode.CreateNew, FileShare.None, totalSize, out int alignment);

        try
        {
            using (output)
            {
                List<Segment> segments = parts.Select((part, i) => new Segment(part, i + 1, part.Path, 0, part.Size)).ToList();

                await CreatePipeline(output, 0, segments, parts.Count, alignment, progress)
                    .RunAsync(cancellationToken)
                    .ConfigureAwait(false);

                RandomAccess.SetLength(output, totalSize);
                RandomAccess.FlushToDisk(output);
            }

            File.Move(partialPath, fullOutputPath, overwrite: true);
            return fullOutputPath;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    // Merges without copying the root file and with little free space
    public async Task<string> MergeInPlaceAsync(
        SplitPackage package,
        string outputPath,
        IProgress<MergeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string fullOutputPath = CheckArguments(package, outputPath);
        string partialPath = fullOutputPath + PartialExtension;

        if (!IsNextToSource(package, fullOutputPath))
            throw new ArgumentException($"Merging in place keeps the output next to the source files, choose an output file in '{package.SourceDirectory}'.");

        Validate(package);

        PackagePart root = package.Root;
        long rootLength = new FileInfo(root.Path).Length;
        if (rootLength != root.Size)
            throw MergePipeline.Changed(root, rootLength);

        ThrowIfPartialExists(partialPath);

        // Fail before renaming anything when the root file can't be appended to (read-only, no permission, whatever)
        try
        {
            File.OpenHandle(root.Path, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"'{root.FileName}' can't be written to (is it read-only?), merging in place appends the pieces to it.", ex);
        }

        IReadOnlyList<PackagePart> parts = package.Parts;
        long totalSize = package.TotalSize;
        long committed = root.Size;
        int mergedPieces = 0;

        File.Move(root.Path, partialPath);

        try
        {
            using (SafeFileHandle output = OpenOutput(partialPath, FileMode.Open, FileShare.Read, 0, out int alignment))
            {
                // Unbuffered writes start on a sector boundary, so the root file's last partial sector is written again.
                long outputStart = MergePipeline.AlignDown(root.Size, alignment);
                List<Segment> segments = [];

                if (outputStart < root.Size)
                    segments.Add(new Segment(root, 1, partialPath, outputStart, root.Size - outputStart, SharedWithOutput: true));

                for (int i = 1; i < parts.Count; i++)
                    segments.Add(new Segment(parts[i], i + 1, parts[i].Path, 0, parts[i].Size));

                if (segments.Count > 0)
                {
                    await CreatePipeline(output, outputStart, segments, parts.Count, alignment, progress, segment =>
                        {
                            if (segment.Part.Kind != PackagePartKind.Piece)
                                return;

                            RandomAccess.FlushToDisk(output);
                            File.Delete(segment.Path);
                            committed += segment.Length;
                            mergedPieces++;
                        })
                        .RunAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                RandomAccess.SetLength(output, totalSize);
                RandomAccess.FlushToDisk(output);
            }

            File.Move(partialPath, fullOutputPath, overwrite: true);
            return fullOutputPath;
        }
        catch (Exception ex)
        {
            string state = mergedPieces == 0
                ? "The source files are unchanged."
                : $"The {Plural.Pieces(mergedPieces)} merged so far {(mergedPieces == 1 ? "stays" : "stay")} in '{root.FileName}' and the remaining pieces were renumbered from 1, merge again to finish.";

            if (TryRestore(package, partialPath, committed, mergedPieces) is { } restoreError)
                throw new IOException($"{ex.Message} {restoreError}", ex);

            if (ex is OperationCanceledException)
                throw new OperationCanceledException(state, ex, cancellationToken);

            if (mergedPieces == 0)
                throw;

            throw new IOException($"{ex.Message} {state}", ex);
        }
    }

    public static string ResolveOutputPath(SplitPackage package, string outputPath)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        return IsFolder(fullOutputPath) ? Path.Combine(fullOutputPath, package.OutputFileName) : fullOutputPath;
    }

    private static bool IsFolder(string fullPath) => Path.EndsInDirectorySeparator(fullPath) || Directory.Exists(fullPath);

    private static bool IsNextToSource(SplitPackage package, string outputPath) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(outputPath)), package.SourceDirectory, PkgFile.PathComparison);

    public static void Validate(SplitPackage package)
    {
        PkgPlatform? platform = PkgFile.GetRootPlatform(PkgFile.ReadMagic(package.Root.Path));

        if (platform is null)
            throw new InvalidDataException(PkgFile.NotPkgMessage(package.Root.FileName));

        if (platform != package.Platform)
            throw new InvalidDataException($"'{package.Root.FileName}' is a {platform.Value.ToDisplayName()} package, but it was scanned as {package.Platform.ToDisplayName()}.");

        if (package.Platform == PkgPlatform.Ps5 && package.Sc is null)
            throw new InvalidDataException($"{package.TitleId} is a PS5 package and can't be merged without its _sc.pkg file.");

        if (package.Platform == PkgPlatform.Ps4 && package.Sc is not null)
            throw new InvalidDataException($"{package.TitleId} is a PS4 package, it doesn't use an _sc.pkg file.");

        if (package.Sc is { } sc && PkgFile.ReadMagic(sc.Path) != PkgMagic.Cnt)
            throw new InvalidDataException(PkgFile.NotScMessage(sc.FileName));

        if (package.Pieces.FirstOrDefault(p => p.Size == 0) is { } empty)
            throw new InvalidDataException($"Piece '{empty.FileName}' is empty (0 bytes).");
    }

    private string CheckArguments(SplitPackage package, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(BufferSize, 1);

        string fullOutputPath = Path.GetFullPath(outputPath);

        if (IsFolder(fullOutputPath))
            throw new ArgumentException($"The output path '{fullOutputPath}' is a folder, not a file.");

        if (package.Parts.Any(p => string.Equals(Path.GetFullPath(p.Path), fullOutputPath, PkgFile.PathComparison)))
            throw new ArgumentException("The output file can't be one of the package's parts.");

        return fullOutputPath;
    }

    private MergePipeline CreatePipeline(
        SafeFileHandle output,
        long outputStart,
        IReadOnlyList<Segment> segments,
        int partCount,
        int alignment,
        IProgress<MergeProgress>? progress,
        Action<Segment>? segmentWritten = null)
    {
        int bufferSize = (int)MergePipeline.AlignUp(BufferSize, MergePipeline.MemoryAlignment);
        return new MergePipeline(output, outputStart, segments, partCount, bufferSize, BufferCount, alignment, progress, segmentWritten);
    }

    // Windows: writes the output past the file cache, so a large merge doesn't fill the memory with data waiting to be
    // written. Falls back to cached writes where the file system doesn't support it or its sectors are larger than 4 KiB.
    private static SafeFileHandle OpenOutput(string path, FileMode mode, FileShare share, long preallocationSize, out int alignment)
    {
        bool createdUnbuffered = false;

        if (OperatingSystem.IsWindows())
        {
            SafeFileHandle? unbuffered = null;
            try
            {
                unbuffered = File.OpenHandle(path, mode, FileAccess.Write, share, FileOptions.Asynchronous | NoBuffering,
                    MergePipeline.AlignUp(preallocationSize, MergePipeline.MemoryAlignment));
            }
            catch (IOException)
            {
                // Not supported by the file system
            }

            if (unbuffered is not null)
            {
                // The pipeline aligns unbuffered writes to 4 KiB, which has to cover whole sectors
                if (SectorSize.Get(unbuffered) <= MergePipeline.MemoryAlignment)
                {
                    alignment = MergePipeline.MemoryAlignment;
                    return unbuffered;
                }

                unbuffered.Dispose();

                createdUnbuffered = mode != FileMode.Open;
                mode = FileMode.Open;
                preallocationSize = 0;
            }
        }

        alignment = 1;
        try
        {
            return File.OpenHandle(path, mode, FileAccess.Write, share, FileOptions.Asynchronous, preallocationSize);
        }
        catch when (createdUnbuffered)
        {
            TryDelete(path);
            throw;
        }
    }

    private static string? TryRestore(SplitPackage package, string partialPath, long committed, int mergedPieces)
    {
        string root = package.Root.FileName;

        try
        {
            if (new FileInfo(partialPath).Length != committed)
            {
                using SafeFileHandle handle = File.OpenHandle(partialPath, FileMode.Open, FileAccess.Write);
                RandomAccess.SetLength(handle, committed);
                RandomAccess.FlushToDisk(handle);
            }

            File.Move(partialPath, package.Root.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Restoring the source files failed too ({ex.Message}). The first {committed} bytes of '{Path.GetFileName(partialPath)}' "
                   + $"are '{root}' and the first {Plural.Pieces(mergedPieces)}, the remaining pieces are untouched.";
        }

        if (mergedPieces == 0)
            return null;

        for (int i = mergedPieces; i < package.Pieces.Count; i++)
        {
            int number = i - mergedPieces + 1;
            string renamed = Path.Combine(package.SourceDirectory, $"{package.TitleId}_{number}{PkgFile.Extension}");

            try
            {
                File.Move(package.Pieces[i].Path, renamed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"Restoring the source files failed too ({ex.Message}). '{root}' holds the first {Plural.Pieces(mergedPieces)}; "
                       + $"rename '{package.Pieces[i].FileName}' and the pieces after it so they continue from {package.TitleId}_{number}{PkgFile.Extension}, then merge again.";
            }
        }

        return null;
    }

    private static void ThrowIfPartialExists(string partialPath)
    {
        if (File.Exists(partialPath))
            throw new IOException($"'{Path.GetFileName(partialPath)}' already exists, it may be left over from an interrupted merge. Move or delete it first.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //
        }
    }
}
