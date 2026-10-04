using System.Buffers.Binary;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>Converts unsigned integers of 1..8 bytes to and from their byte form in a given <see cref="Endianness"/>, and reorders bytes.</summary>
public static class EndianCodec
{
    /// <summary>The byte order of the machine this code runs on.</summary>
    public static Endianness Native => BitConverter.IsLittleEndian ? Endianness.Little : Endianness.Big;

    /// <summary>Writes the lowest <paramref name="destination"/>.Length bytes of <paramref name="value"/>.</summary>
    public static void Write(ulong value, Span<byte> destination, Endianness endianness)
    {
        ValidateSize(destination.Length);
        Span<byte> little = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(little, value);

        little[..destination.Length].CopyTo(destination);
        if (endianness == Endianness.Big)
        {
            destination.Reverse();
        }
    }

    /// <summary>Reads <paramref name="source"/>.Length bytes as an unsigned integer.</summary>
    public static ulong Read(ReadOnlySpan<byte> source, Endianness endianness)
    {
        ValidateSize(source.Length);
        ulong value = 0;
        if (endianness == Endianness.Big)
        {
            foreach (var b in source)
            {
                value = (value << 8) | b;
            }
        }
        else
        {
            for (var i = source.Length - 1; i >= 0; i--)
            {
                value = (value << 8) | source[i];
            }
        }
        return value;
    }

    /// <summary>Reverses the bytes in place when <paramref name="from"/> and <paramref name="to"/> differ.</summary>
    public static void ConvertInPlace(Span<byte> bytes, Endianness from, Endianness to)
    {
        if (from != to)
        {
            bytes.Reverse();
        }
    }

    /// <summary>Returns a copy of <paramref name="bytes"/> converted from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static byte[] Convert(ReadOnlySpan<byte> bytes, Endianness from, Endianness to)
    {
        var result = bytes.ToArray();
        ConvertInPlace(result, from, to);
        return result;
    }

    private static void ValidateSize(int sizeInBytes)
    {
        if (sizeInBytes is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes, "Only 1..8 bytes are supported.");
        }
    }
}
