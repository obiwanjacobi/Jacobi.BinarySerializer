using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class VarIntProcessorTests
{
    [TestCase(0UL, new byte[] { 0x00 })]
    [TestCase(127UL, new byte[] { 0x7F })]
    [TestCase(128UL, new byte[] { 0x80, 0x01 })]
    [TestCase(300UL, new byte[] { 0xAC, 0x02 })]
    public void Codec_Unsigned_KnownVectors(ulong value, byte[] expected)
    {
        Assert.That(VarIntCodec.EncodeUnsigned(value), Is.EqualTo(expected));
        Assert.That(VarIntCodec.TryDecodeUnsigned(expected, out var decoded, out var length), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(length, Is.EqualTo(expected.Length));
    }

    [TestCase(0L, new byte[] { 0x00 })]
    [TestCase(-1L, new byte[] { 0x7F })]
    [TestCase(63L, new byte[] { 0x3F })]
    [TestCase(64L, new byte[] { 0xC0, 0x00 })]
    [TestCase(-64L, new byte[] { 0x40 })]
    [TestCase(-65L, new byte[] { 0xBF, 0x7F })]
    [TestCase(-123456L, new byte[] { 0xC0, 0xBB, 0x78 })]
    public void Codec_Signed_KnownVectors(long value, byte[] expected)
    {
        Assert.That(VarIntCodec.EncodeSigned(value), Is.EqualTo(expected));
        Assert.That(VarIntCodec.TryDecodeSigned(expected, out var decoded, out var length), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(length, Is.EqualTo(expected.Length));
    }

    [TestCase(0L, new byte[] { 0x00 })]
    [TestCase(-1L, new byte[] { 0x01 })]
    [TestCase(1L, new byte[] { 0x02 })]
    [TestCase(-2L, new byte[] { 0x03 })]
    [TestCase(150L, new byte[] { 0xAC, 0x02 })]
    public void Codec_ZigZag_KnownVectors(long value, byte[] expected)
    {
        Assert.That(VarIntCodec.EncodeZigZag(value), Is.EqualTo(expected));
        Assert.That(VarIntCodec.TryDecodeZigZag(expected, out var decoded, out _), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
    }

    [Test]
    public void Codec_Extremes_RoundTrip()
    {
        foreach (var value in new[] { Int64.MinValue, Int64.MaxValue, 0L, -1L })
        {
            Assert.That(VarIntCodec.TryDecodeSigned(VarIntCodec.EncodeSigned(value), out var s, out _), Is.True);
            Assert.That(s, Is.EqualTo(value));
            Assert.That(VarIntCodec.TryDecodeZigZag(VarIntCodec.EncodeZigZag(value), out var z, out _), Is.True);
            Assert.That(z, Is.EqualTo(value));
        }

        Assert.That(VarIntCodec.TryDecodeUnsigned(VarIntCodec.EncodeUnsigned(UInt64.MaxValue), out var u, out _), Is.True);
        Assert.That(u, Is.EqualTo(UInt64.MaxValue));
    }

    [Test]
    public void Codec_Incomplete_Fails()
    {
        Assert.That(VarIntCodec.TryDecodeUnsigned(new byte[] { 0x80 }, out _, out _), Is.False);
        Assert.That(VarIntCodec.TryDecodeSigned(new byte[] { 0xFF, 0xFF }, out _, out _), Is.False);
    }

    [Test]
    public void RoundTrip_DefaultIsUnsignedLeb128_FieldsKeepTheirBoundaries()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.UInt32, [Ref("varint")]),
            Field("B", SchemaDataType.UInt8),
            Field("C", SchemaDataType.UInt16, [Ref("varint")]));
        var values = new Dictionary<string, object?> { ["Root.A"] = 300u, ["Root.B"] = (byte)7, ["Root.C"] = (ushort)5 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAC, 0x02, 7, 5 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_SLeb128_NegativeValue()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.Int32, [Ref("varint", ("encoding", "sleb128"))]),
            Field("B", SchemaDataType.UInt8));
        var values = new Dictionary<string, object?> { ["Root.A"] = -123456, ["Root.B"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC0, 0xBB, 0x78, 9 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_ZigZag_NegativeValue()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.Int16, [Ref("varint", ("encoding", "zigzag"))]));
        var values = new Dictionary<string, object?> { ["Root.A"] = (short)-2 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x03 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_SignedTypeWithLeb128_Throws()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.Int32, [Ref("varint")]));

        Assert.Throws<InvalidOperationException>(() =>
            Write(root, new Dictionary<string, object?> { ["Root.A"] = 1 }, out _));
    }

    [Test]
    public void Write_InvalidEncoding_Throws()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.UInt32, [Ref("varint", ("encoding", "bogus"))]));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            Write(root, new Dictionary<string, object?> { ["Root.A"] = 1u }, out _));
        Assert.That(ex!.Message, Does.Contain("sys:varint.encoding"));
    }
}
