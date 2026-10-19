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

    public static LayoutReadResult<ReadOnlyMemory<byte>> PassThroughRead(ref SequenceReader<byte> reader, int? byteLength = null)
    {
        var length = byteLength ?? checked((int)reader.Remaining);
        if (length == 0 || reader.Remaining < length)
        {
            return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData(byteLength);
        }
        var output = reader.UnreadSequence.Slice(0, length);
        var bytes = output.IsSingleSegment ? output.First : output.ToArray();
        reader.Advance(length);
        return LayoutReadResult<ReadOnlyMemory<byte>>.Success(bytes);
    }

    public static WriteResult PassThroughWrite(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        writer.Write(bytes);
        return WriteResult.Success;
    }
}
