using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace PKGMerge.Core;

internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private readonly int _length;
    private void* _pointer;

    public AlignedBuffer(int length, int alignment)
    {
        _length = length;
        _pointer = NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
    }

    public override Span<byte> GetSpan() => new(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer is null)
            return;

        NativeMemory.AlignedFree(_pointer);
        _pointer = null;
    }
}
