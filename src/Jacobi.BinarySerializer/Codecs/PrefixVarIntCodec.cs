using System.Numerics;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Prefix varint (UTF-8 / Matroska style): the number of leading 1-bits in the first byte is the number of bytes that follow it.
/// The remaining bits of the first byte are the most significant bits of the value; the following bytes are big-endian.
/// 1 to 8 bytes carry 7 bits each (7, 14, ... 56 bits); a first byte of 0xFF is followed by 8 raw bytes (64 bits, 9 bytes in total).
/// The length is known from the first byte alone. Unsigned.
/// </summary>
public static class PrefixVarIntCodec
{
    /// <summary>The longest encoding of a 64-bit value.</summary>
    public const int MaxLength = 9;

    public static byte[] Encode(ulong value)
    {
        var length = 1;
        while (length < MaxLength && value >= (1UL << (7 * length)))
        {
            length++;
        }

        var bytes = new byte[length];
        if (length == MaxLength)
        {
            bytes[0] = 0xFF;
            for (var i = MaxLength - 1; i >= 1; i--)
            {
                bytes[i] = (byte)value;
                value >>= 8;
            }
            return bytes;
        }

        for (var i = length - 1; i >= 1; i--)
        {
            bytes[i] = (byte)value;
            value >>= 8;
        }

        // length - 1 leading ones followed by a zero.
        var prefix = (byte)(0xFF00 >> (length - 1));
        bytes[0] = (byte)(prefix | (byte)value);
        return bytes;
    }

    /// <summary>Returns false when the bytes are shorter than the length that the first byte announces.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (bytes.IsEmpty)
        {
            return false;
        }

        var first = bytes[0];
        var total = BitOperations.LeadingZeroCount((uint)(byte)~first) - 24 + 1;
        if (bytes.Length < total)
        {
            return false;
        }

        var result = total < MaxLength ? (ulong)(first & (0xFF >> total)) : 0UL;
        for (var i = 1; i < total; i++)
        {
            result = (result << 8) | bytes[i];
        }

        value = result;
        length = total;
        return true;
    }
}
