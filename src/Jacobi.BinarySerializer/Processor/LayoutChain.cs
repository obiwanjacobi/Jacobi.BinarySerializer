using System.Buffers;

namespace Jacobi.BinarySerializer.Processor;

/// <summary>Helpers for processors that implement the chained layout interfaces.</summary>
public static class LayoutChain
{
    /// <summary>The unread input of the reader, to return from a chained Read (no copy for a single segment).</summary>
    public static ReadOnlyMemory<byte> Unread(in SequenceReader<byte> reader)
    {
        var unread = reader.UnreadSequence;
        return unread.IsSingleSegment ? unread.First : unread.ToArray();
    }

    public static LayoutReadResult<ReadOnlyMemory<byte>> PassThroughRead(ref SequenceReader<byte> reader)
        => LayoutReadResult<ReadOnlyMemory<byte>>.Success(LayoutChain.Unread(reader));

    public static WriteResult PassThroughWrite(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        writer.Write(bytes);
        return WriteResult.Success;
    }
}
