namespace Jacobi.BinarySerializer.Codecs;

/// <summary>ZigZag mapping followed by unsigned LEB128 (protobuf sint32/sint64).</summary>
public static class ZigZagCodec
{
    /// <summary>The longest encoding of a 64-bit value.</summary>
    public const int MaxLength = Leb128Codec.MaxLength;

    /// <summary>Maps signed to unsigned so that small magnitudes stay small: 0, -1, 1, -2 become 0, 1, 2, 3.</summary>
    public static ulong Map(long value) => (ulong)((value << 1) ^ (value >> 63));

    /// <summary>The inverse of <see cref="Map"/>.</summary>
    public static long Unmap(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    public static byte[] Encode(long value) => Leb128Codec.Encode(Map(value));

    /// <summary>Returns false when the bytes are incomplete or the value does not fit 64 bits.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out long value, out int length)
    {
        if (Leb128Codec.TryDecode(bytes, out var unsigned, out length))
        {
            value = Unmap(unsigned);
            return true;
        }

        value = 0;
        return false;
    }
}
