namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Joins the lowest <c>bits</c> bits of each of <c>byteCount</c> bytes into one unsigned value and back
/// (for example 7 bits per byte: MIDI 14-bit values, sync-safe integers; 8 bits per byte is a plain integer).
/// The unused high bits of each byte are zero.
/// </summary>
public static class BitSlicerCodec
{
    public const int MaxTotalBits = 64;

    /// <summary>True when 1..8 bits are taken from each of at least one byte and the total fits 64 bits.</summary>
    public static bool IsValid(int bits, int byteCount)
        => bits is >= 1 and <= 8 && byteCount >= 1 && (long)bits * byteCount <= MaxTotalBits;

    /// <summary>
    /// Splits the value into <paramref name="byteCount"/> slices of <paramref name="bits"/> bits.
    /// <see cref="Endianness.Little"/> stores the least significant slice first.
    /// Throws when the value does not fit.
    /// </summary>
    public static byte[] Encode(ulong value, int bits, int byteCount, Endianness order)
    {
        Validate(bits, byteCount);
        var totalBits = bits * byteCount;
        if (totalBits < MaxTotalBits && (value >> totalBits) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"The value does not fit in {byteCount} bytes of {bits} bits.");
        }

        var mask = (1 << bits) - 1;
        var bytes = new byte[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            var slice = (byte)((value >> (i * bits)) & (uint)mask);
            bytes[order == Endianness.Little ? i : byteCount - 1 - i] = slice;
        }

        return bytes;
    }

    /// <summary>Returns false when a byte has a bit set above <paramref name="bits"/>.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, int bits, Endianness order, out ulong value)
    {
        Validate(bits, bytes.Length);
        value = 0;
        var byteCount = bytes.Length;
        ulong result = 0;
        for (var i = 0; i < byteCount; i++)
        {
            var b = bytes[order == Endianness.Little ? i : byteCount - 1 - i];
            if ((b >> bits) != 0)
            {
                return false;
            }
            result |= (ulong)b << (i * bits);
        }

        value = result;
        return true;
    }

    private static void Validate(int bits, int byteCount)
    {
        if (!IsValid(bits, byteCount))
        {
            throw new ArgumentOutOfRangeException(nameof(bits), $"{bits} bits of {byteCount} bytes is not valid: 1 to 8 bits per byte, at most {MaxTotalBits} bits in total.");
        }
    }
}
