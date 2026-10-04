namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Variable-length quantity (MIDI, ASN.1 BER subidentifiers): 7 bits per byte, most significant group first,
/// bit 7 = 'more bytes follow'. Unsigned.
/// </summary>
public static class VlqCodec
{
    /// <summary>The longest encoding of a 64-bit value.</summary>
    public const int MaxLength = 10;

    public static byte[] Encode(ulong value)
    {
        Span<byte> buffer = stackalloc byte[MaxLength];
        var position = MaxLength;
        buffer[--position] = (byte)(value & 0x7F);
        value >>= 7;
        while (value != 0)
        {
            buffer[--position] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        return buffer[position..].ToArray();
    }

    /// <summary>Returns false when the bytes are incomplete or the value does not fit 64 bits.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ulong value, out int length)
    {
        ulong result = 0;
        value = 0;
        length = 0;
        for (var i = 0; i < bytes.Length && i < MaxLength; i++)
        {
            if (result > (UInt64.MaxValue >> 7))
            {
                return false;
            }

            var b = bytes[i];
            result = (result << 7) | (ulong)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                value = result;
                length = i + 1;
                return true;
            }
        }

        return false;
    }
}
