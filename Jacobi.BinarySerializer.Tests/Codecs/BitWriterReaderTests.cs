using System.Buffers;
using Jacobi.BinarySerializer.Codecs;

namespace Jacobi.BinarySerializer.Tests.Codecs;

public class BitWriterReaderTests
{
    [Test]
    public void Write_LsbFirst_PacksFromLowBits()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BitWriter(buffer, Endianness.Little);
        writer.Write(0b101, 3);
        writer.Write(0b01, 2);
        writer.Flush();

        Assert.That(buffer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0b0000_1101 }));
    }

    [Test]
    public void Write_MsbFirst_PacksFromHighBits()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BitWriter(buffer, Endianness.Big);
        writer.Write(0b101, 3);
        writer.Write(0b01, 2);
        writer.Flush();

        Assert.That(buffer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0xA8 }));
    }

    [TestCase(Endianness.Little)]
    [TestCase(Endianness.Big)]
    public void RoundTrip_AcrossByteBoundaries(Endianness order)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BitWriter(buffer, order);
        writer.Write(0x5, 3);
        writer.Write(0x1234, 13);
        writer.Write(0xDEADBEEFCAFEUL, 48);
        writer.Flush();

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var bits = new BitReader(order);
        Assert.That(bits.TryRead(ref reader, 3, out var a), Is.True);
        Assert.That(bits.TryRead(ref reader, 13, out var b), Is.True);
        Assert.That(bits.TryRead(ref reader, 48, out var c), Is.True);

        Assert.That(a, Is.EqualTo(0x5UL));
        Assert.That(b, Is.EqualTo(0x1234UL));
        Assert.That(c, Is.EqualTo(0xDEADBEEFCAFEUL));
    }

    [Test]
    public void TryRead_NotEnoughData_ConsumesNothing()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 0xFF }));
        var bits = new BitReader();

        Assert.That(bits.TryRead(ref reader, 12, out _), Is.False);
        Assert.That(reader.Remaining, Is.EqualTo(1));
        Assert.That(bits.TryRead(ref reader, 8, out var value), Is.True);
        Assert.That(value, Is.EqualTo(0xFFUL));
    }
}
