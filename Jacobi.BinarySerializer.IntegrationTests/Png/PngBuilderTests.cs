using System.Buffers.Binary;
using System.IO.Compression;

namespace Jacobi.BinarySerializer.IntegrationTests.Png;

public class PngBuilderTests
{
    [Test]
    public void Grayscale8_StartsWithSignature()
    {
        var png = PngBuilder.Grayscale8(4, 4);

        BinaryAssert.AreEqual(PngBuilder.Signature, png.AsSpan(0, 8));
    }

    [Test]
    public void Grayscale8_HasIhdrIdatIendChunks()
    {
        var png = PngBuilder.Grayscale8(4, 4);
        var types = new List<string>();

        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset));
            types.Add(System.Text.Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }

        Assert.That(types, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));
        Assert.That(offset, Is.EqualTo(png.Length));
    }

    [Test]
    public void Crc32_MatchesKnownIendCrc()
    {
        // The CRC of an empty IEND chunk is a well known constant.
        var crc = PngBuilder.Crc32(0, "IEND"u8);

        Assert.That(crc, Is.EqualTo(0xAE426082u));
    }

    [Test]
    public void BinaryAssert_ReportsFirstDifferingOffset()
    {
        var ex = Assert.Throws<AssertionException>(
            () => BinaryAssert.AreEqual([1, 2, 3, 4], [1, 2, 9, 4]));

        Assert.That(ex!.Message, Does.Contain("offset 2"));
    }

    [Test]
    public void BinaryAssert_ReportsLengthMismatch()
    {
        Assert.Throws<AssertionException>(() => BinaryAssert.AreEqual([1, 2, 3], [1, 2]));
    }
}
