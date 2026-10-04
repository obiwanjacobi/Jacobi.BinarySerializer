namespace Jacobi.BinarySerializer.Codecs;

/// <summary>Signed LEB128: two's complement, sign-extended from bit 6 of the last byte.</summary>
public static class Sleb128Codec
{
    /// <summary>The longest encoding of a 64-bit value.</summary>
    public const int MaxLength = 10;

    public static byte[] Encode(long value)
    {
        Span<byte> buffer = stackalloc byte[MaxLength];
        var count = 0;
        var more = true;
        while (more)
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if ((value == 0 && (b & 0x40) == 0) || (value == -1 && (b & 0x40) != 0))
            {
                more = false;
            }
            else
            {
                b |= 0x80;
            }
            buffer[count++] = b;
        }

        return buffer[..count].ToArray();
    }

    /// <summary>Returns false when the bytes are incomplete or the value does not fit 64 bits.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out long value, out int length)
    {
        ulong result = 0;
        value = 0;
        length = 0;
        for (var i = 0; i < bytes.Length && i < MaxLength; i++)
        {
            var b = bytes[i];
            var sign = (b & 0x40) != 0;
            if (i == MaxLength - 1)
            {
                // only bit 63 is left: the remaining bits must all equal the sign.
                if ((b & 0x80) != 0 || (b & 0x7E) != (sign ? 0x7E : 0) || (b & 1) != 0 != sign)
                {
                    return false;
                }
            }

            result |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                var shift = 7 * (i + 1);
                if (sign && shift < 64)
                {
                    result |= ~0UL << shift;
                }

                value = (long)result;
                length = i + 1;
                return true;
            }
        }

        return false;
    }
}
