using System.Buffers;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>Variable-length integer algorithms: 7 bits per byte, least significant group first, bit 7 = 'more bytes follow'.</summary>
public static class VarIntCodec
{
    /// <summary>The longest encoding of a 64-bit value.</summary>
    public const int MaxLength = 10;

    /// <summary>Unsigned LEB128.</summary>
    public static byte[] EncodeUnsigned(ulong value)
    {
        Span<byte> buffer = stackalloc byte[MaxLength];
        var count = 0;
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
            {
                b |= 0x80;
            }
            buffer[count++] = b;
        } while (value != 0);

        return buffer[..count].ToArray();
    }

    /// <summary>Signed LEB128 (two's complement, sign-extended from bit 6 of the last byte).</summary>
    public static byte[] EncodeSigned(long value)
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

    public static ulong ZigZagEncode(long value) => (ulong)((value << 1) ^ (value >> 63));
    public static long ZigZagDecode(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>ZigZag mapping followed by unsigned LEB128 (protobuf sint32/sint64).</summary>
    public static byte[] EncodeZigZag(long value) => EncodeUnsigned(ZigZagEncode(value));

    /// <summary>Returns false when the bytes are incomplete or the value does not fit 64 bits.</summary>
    public static bool TryDecodeUnsigned(ReadOnlySpan<byte> bytes, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        for (var i = 0; i < bytes.Length && i < MaxLength; i++)
        {
            var b = bytes[i];
            if (i == MaxLength - 1 && b > 1)
            {
                return false;
            }

            value |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                length = i + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns false when the bytes are incomplete or the value does not fit 64 bits.</summary>
    public static bool TryDecodeSigned(ReadOnlySpan<byte> bytes, out long value, out int length)
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

    public static bool TryDecodeZigZag(ReadOnlySpan<byte> bytes, out long value, out int length)
    {
        if (TryDecodeUnsigned(bytes, out var unsigned, out length))
        {
            value = ZigZagDecode(unsigned);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Finds the length of the first variable-length integer in the input (all algorithms here share the continuation bit).
    /// Returns false when the end of the input is reached before the last byte.
    /// </summary>
    public static bool TryMeasure(in ReadOnlySequence<byte> input, out int length)
    {
        length = 0;
        foreach (var segment in input)
        {
            foreach (var b in segment.Span)
            {
                length++;
                if ((b & 0x80) == 0 || length >= MaxLength)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
