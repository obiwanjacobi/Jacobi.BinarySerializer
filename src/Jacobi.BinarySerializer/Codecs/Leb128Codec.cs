namespace Jacobi.BinarySerializer.Codecs;

/// <summary>Unsigned LEB128: 7 bits per byte, least significant group first, bit 7 = 'more bytes follow'.</summary>
public static class Leb128Codec
{
    /// <summary>The longest encoding of a 64-bit value.</summary>
    public const int MaxLength = 10;

    public static byte[] Encode(ulong value)
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

    /// <summary>Returns false when the bytes are incomplete or the value does not fit 64 bits.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out ulong value, out int length)
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
}
