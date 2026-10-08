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
        Assert.That(Leb128Codec.Encode(value), Is.EqualTo(expected));
        Assert.That(Leb128Codec.TryDecode(expected, out var decoded, out var length), Is.True);
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
        Assert.That(Sleb128Codec.Encode(value), Is.EqualTo(expected));
        Assert.That(Sleb128Codec.TryDecode(expected, out var decoded, out var length), Is.True);
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
        Assert.That(ZigZagCodec.Encode(value), Is.EqualTo(expected));
        Assert.That(ZigZagCodec.TryDecode(expected, out var decoded, out _), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
    }

    [Test]
    public void Codec_Extremes_RoundTrip()
    {
        foreach (var value in new[] { Int64.MinValue, Int64.MaxValue, 0L, -1L })
        {
            Assert.That(Sleb128Codec.TryDecode(Sleb128Codec.Encode(value), out var s, out _), Is.True);
            Assert.That(s, Is.EqualTo(value));
            Assert.That(ZigZagCodec.TryDecode(ZigZagCodec.Encode(value), out var z, out _), Is.True);
            Assert.That(z, Is.EqualTo(value));
        }

        Assert.That(Leb128Codec.TryDecode(Leb128Codec.Encode(UInt64.MaxValue), out var u, out _), Is.True);
        Assert.That(u, Is.EqualTo(UInt64.MaxValue));
    }

    [Test]
    public void Codec_Incomplete_Fails()
    {
        Assert.That(Leb128Codec.TryDecode(new byte[] { 0x80 }, out _, out _), Is.False);
        Assert.That(Sleb128Codec.TryDecode(new byte[] { 0xFF, 0xFF }, out _, out _), Is.False);
    }

    [Test]
    public void RoundTrip_DefaultIsUnsignedLeb128_FieldsKeepTheirBoundaries()
    {
        var root = Group("Root", [],
            Field("A", "UInt32", [Ref("varint")]),
            Field("B", "UInt8"),
            Field("C", "UInt16", [Ref("varint")]));
        var values = new Dictionary<string, object?> { ["Root.A"] = 300u, ["Root.B"] = (byte)7, ["Root.C"] = (ushort)5 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAC, 0x02, 7, 5 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_SLeb128_NegativeValue()
    {
        var root = Group("Root", [],
            Field("A", "Int32", [Ref("varint", ("encoding", "sleb128"))]),
            Field("B", "UInt8"));
        var values = new Dictionary<string, object?> { ["Root.A"] = -123456, ["Root.B"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC0, 0xBB, 0x78, 9 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_ZigZag_NegativeValue()
    {
        var root = Group("Root", [], Field("A", "Int16", [Ref("varint", ("encoding", "zigzag"))]));
        var values = new Dictionary<string, object?> { ["Root.A"] = (short)-2 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x03 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [TestCase(0UL, new byte[] { 0x00 })]
    [TestCase(127UL, new byte[] { 0x7F })]
    [TestCase(128UL, new byte[] { 0x81, 0x00 })]
    [TestCase(16383UL, new byte[] { 0xFF, 0x7F })]
    [TestCase(2097152UL, new byte[] { 0x81, 0x80, 0x80, 0x00 })]
    public void Codec_Vlq_KnownVectors(ulong value, byte[] expected)
    {
        Assert.That(VlqCodec.Encode(value), Is.EqualTo(expected));
        Assert.That(VlqCodec.TryDecode(expected, out var decoded, out var length), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(length, Is.EqualTo(expected.Length));
    }

    [TestCase(0UL, new byte[] { 0x00 })]
    [TestCase(127UL, new byte[] { 0x7F })]
    [TestCase(128UL, new byte[] { 0x80, 0x80 })]
    [TestCase(300UL, new byte[] { 0x81, 0x2C })]
    [TestCase(16383UL, new byte[] { 0xBF, 0xFF })]
    [TestCase(16384UL, new byte[] { 0xC0, 0x40, 0x00 })]
    public void Codec_Prefix_KnownVectors(ulong value, byte[] expected)
    {
        Assert.That(PrefixVarIntCodec.Encode(value), Is.EqualTo(expected));
        Assert.That(PrefixVarIntCodec.TryDecode(expected, out var decoded, out var length), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(length, Is.EqualTo(expected.Length));
    }

    [Test]
    public void Codec_VlqAndPrefix_Extremes_RoundTrip()
    {
        var values = new List<ulong> { 0, UInt64.MaxValue, (1UL << 56) - 1, 1UL << 56 };
        for (var shift = 1; shift < 64; shift++)
        {
            values.Add(1UL << shift);
            values.Add((1UL << shift) - 1);
        }

        foreach (var value in values)
        {
            var vlq = VlqCodec.Encode(value);
            Assert.That(VlqCodec.TryDecode(vlq, out var v, out var vl), Is.True, $"vlq {value}");
            Assert.That(v, Is.EqualTo(value));
            Assert.That(vl, Is.EqualTo(vlq.Length));

            var prefix = PrefixVarIntCodec.Encode(value);
            Assert.That(PrefixVarIntCodec.TryDecode(prefix, out var p, out var pl), Is.True, $"prefix {value}");
            Assert.That(p, Is.EqualTo(value));
            Assert.That(pl, Is.EqualTo(prefix.Length));
        }

        Assert.That(VlqCodec.Encode(UInt64.MaxValue), Has.Length.EqualTo(10));
        Assert.That(PrefixVarIntCodec.Encode(UInt64.MaxValue), Has.Length.EqualTo(9));
    }

    [Test]
    public void Codec_VlqAndPrefix_Incomplete_Fails()
    {
        Assert.That(VlqCodec.TryDecode(new byte[] { 0x81 }, out _, out _), Is.False);
        Assert.That(PrefixVarIntCodec.TryDecode(new byte[] { 0xC0, 0x40 }, out _, out _), Is.False);
        Assert.That(PrefixVarIntCodec.TryDecode(ReadOnlySpan<byte>.Empty, out _, out _), Is.False);
    }

    [TestCase("vlq", new byte[] { 0x81, 0x00, 7 })]
    [TestCase("prefix", new byte[] { 0x80, 0x80, 7 })]
    public void RoundTrip_VlqAndPrefix_FieldsKeepTheirBoundaries(string encoding, byte[] expected)
    {
        var root = Group("Root", [],
            Field("A", "UInt32", [Ref("varint", ("encoding", encoding))]),
            Field("B", "UInt8"));
        var values = new Dictionary<string, object?> { ["Root.A"] = 128u, ["Root.B"] = (byte)7 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(expected));
        Assert.That(read, Is.EqualTo(values));
    }

    [TestCase("vlq")]
    [TestCase("prefix")]
    public void Write_SignedTypeWithUnsignedEncoding_Throws(string encoding)
    {
        var root = Group("Root", [], Field("A", "Int32", [Ref("varint", ("encoding", encoding))]));

        Assert.Throws<InvalidOperationException>(() =>
            Write(root, new Dictionary<string, object?> { ["Root.A"] = 1 }, out _));
    }

    [Test]
    public void Write_SignedTypeWithLeb128_Throws()
    {
        var root = Group("Root", [], Field("A", "Int32", [Ref("varint")]));

        Assert.Throws<InvalidOperationException>(() =>
            Write(root, new Dictionary<string, object?> { ["Root.A"] = 1 }, out _));
    }

    [Test]
    public void Write_InvalidEncoding_Throws()
    {
        var root = Group("Root", [], Field("A", "UInt32", [Ref("varint", ("encoding", "bogus"))]));

        var ex = Assert.Throws<Jacobi.BinarySerializer.Execution.ExecutionPlanException>(() =>
            Write(root, new Dictionary<string, object?> { ["Root.A"] = 1u }, out _));
        Assert.That(ex!.Message, Does.Contain("sys.varint.encoding"));
    }
}
