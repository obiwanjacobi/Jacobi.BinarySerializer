using Jacobi.BinarySerializer.Codecs;

namespace Jacobi.BinarySerializer.Tests.Codecs;

public class EndianCodecTests
{
    [TestCase(Endianness.Little, new byte[] { 0x34, 0x12 })]
    [TestCase(Endianness.Big, new byte[] { 0x12, 0x34 })]
    public void Write_UsesEndianness(Endianness endianness, byte[] expected)
    {
        var bytes = new byte[2];
        EndianCodec.Write(0x1234, bytes, endianness);

        Assert.That(bytes, Is.EqualTo(expected));
        Assert.That(EndianCodec.Read(bytes, endianness), Is.EqualTo(0x1234UL));
    }

    [Test]
    public void Convert_ReversesOnlyWhenDifferent()
    {
        var bytes = new byte[] { 1, 2, 3 };

        Assert.That(EndianCodec.Convert(bytes, Endianness.Little, Endianness.Little), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(EndianCodec.Convert(bytes, Endianness.Little, Endianness.Big), Is.EqualTo(new byte[] { 3, 2, 1 }));
    }
}
