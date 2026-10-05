using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Jacobi.BinarySerializer.IntegrationTests.Png;

/// <summary>
/// Generates tiny, valid PNG files in code so no external (licensed) test data is needed.
/// </summary>
internal static class PngBuilder
{
    public static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Builds a width x height 8-bit grayscale image: signature, IHDR, IDAT and IEND.
    /// </summary>
    public static byte[] Grayscale8(int width, int height, Func<int, int, byte>? pixel = null)
    {
        pixel ??= (x, y) => (byte)((x + y) * 16);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 0;    // color type: grayscale
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace

        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width + 1);
            raw[row] = 0;   // filter type: none
            for (var x = 0; x < width; x++)
                raw[row + 1 + x] = pixel(x, y);
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", Deflate(raw));
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    public static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
        stream.Write(buffer);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crc = Crc32(Crc32(0, typeBytes), data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        stream.Write(buffer);
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>
    /// Incremental CRC-32 (pass the previous result as <paramref name="crc"/>, start with 0).
    /// </summary>
    public static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        var c = crc ^ 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
