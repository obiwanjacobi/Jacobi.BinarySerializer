using System.Buffers;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>Forwards to the inner writer and counts the bytes written (the layout position).</summary>
internal sealed class CountingBufferWriter(IBufferWriter<byte> inner, long start = 0) : IBufferWriter<byte>
{
    public long Written { get; private set; } = start;

    public void Advance(int count)
    {
        inner.Advance(count);
        Written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
}
