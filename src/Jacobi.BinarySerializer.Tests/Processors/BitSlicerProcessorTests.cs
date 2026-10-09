using Jacobi.BinarySerializer.Codecs;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class BitSlicerProcessorTests
{
    private static Jacobi.BinarySerializer.Schema.SchemaGroup Root(string type, int byteLength, int bits, string order = "little")
        => Group("Root", [], Field("A", type, [Ref("bitslicer", ("bytelength", byteLength.ToString()), ("bits", bits.ToString()), ("byteorder", order))]));

    [TestCase(0x2000UL, 7, 2, Endianness.Little, new byte[] { 0x00, 0x40 })]
    [TestCase(0x3FFFUL, 7, 2, Endianness.Little, new byte[] { 0x7F, 0x7F })]
    [TestCase(0x1234UL, 7, 2, Endianness.Little, new byte[] { 52, 36 })]
    [TestCase(0x1234UL, 7, 2, Endianness.Big, new byte[] { 36, 52 })]
    [TestCase(257UL, 7, 4, Endianness.Big, new byte[] { 0, 0, 2, 1 })]
    [TestCase(0x0102UL, 8, 2, Endianness.Big, new byte[] { 1, 2 })]
    [TestCase(5UL, 3, 2, Endianness.Little, new byte[] { 5, 0 })]
    public void Codec_KnownVectors(ulong value, int bits, int byteCount, Endianness order, byte[] expected)
    {
        Assert.That(BitSlicerCodec.Encode(value, bits, byteCount, order), Is.EqualTo(expected));
        Assert.That(BitSlicerCodec.TryDecode(expected, bits, order, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
    }

    [Test]
    public void Codec_Decode_HighBitSet_Fails()
        => Assert.That(BitSlicerCodec.TryDecode(new byte[] { 0x80, 0x00 }, 7, Endianness.Little, out _), Is.False);

    [Test]
    public void Codec_Encode_ValueTooLarge_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => BitSlicerCodec.Encode(0x4000, 7, 2, Endianness.Little));

    [TestCase(0, 2)]
    [TestCase(9, 2)]
    [TestCase(7, 0)]
    [TestCase(8, 9)]
    public void Codec_InvalidArguments_Throw(int bits, int byteCount)
        => Assert.Throws<ArgumentOutOfRangeException>(() => BitSlicerCodec.Encode(0, bits, byteCount, Endianness.Little));

    [TestCase("little", new byte[] { 0x00, 0x40 })]
    [TestCase("big", new byte[] { 0x40, 0x00 })]
    public void RoundTrip_PitchBend_14Bit(string order, byte[] expected)
    {
        var values = new Dictionary<string, object?> { ["Root.A"] = (ushort)0x2000 };

        var (bytes, read) = RoundTrip(Root("UInt16", 2, 7, order), values);

        Assert.That(bytes, Is.EqualTo(expected));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_SyncSafe_28Bit()
    {
        var values = new Dictionary<string, object?> { ["Root.A"] = 257u };

        var (bytes, read) = RoundTrip(Root("UInt32", 4, 7, "big"), values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0, 0, 2, 1 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_FieldsKeepTheirBoundaries()
    {
        var root = Group("Root", [],
            Field("A", "UInt16", [Ref("bitslicer", ("bytelength", "2"))]),
            Field("B", "UInt8"));
        var values = new Dictionary<string, object?> { ["Root.A"] = (ushort)300, ["Root.B"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 44, 2, 9 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_FieldByteLength_UsedWithoutProperty()
    {
        var field = Field("A", "UInt16", [Ref("bitslicer")]);
        var root = Group("Root", [], new Jacobi.BinarySerializer.Schema.SchemaField
        {
            Name = field.Name,
            DataType = field.DataType,
            ByteLength = 2,
            ProcessorsList = field.ProcessorsList,
        });
        var values = new Dictionary<string, object?> { ["Root.A"] = (ushort)0x2000 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x00, 0x40 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_ValueTooLarge_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Write(Root("UInt16", 2, 7), new() { ["Root.A"] = (ushort)0x4000 }, out _));
    }

    [Test]
    public void Read_HighBitSet_Throws()
    {
        var plan = Build(Root("UInt16", 2, 7));

        Assert.That(() => new Jacobi.BinarySerializer.Execution.ReaderSession(plan).Read(
            new System.Buffers.ReadOnlySequence<byte>(new byte[] { 0x80, 0x00 }), new Jacobi.BinarySerializer.Tests.Execution.DictSink()),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Read_NotEnoughData_NeedsMoreData()
    {
        var plan = Build(Root("UInt16", 2, 7));

        var result = new Jacobi.BinarySerializer.Execution.ReaderSession(plan).Read(
            new System.Buffers.ReadOnlySequence<byte>(new byte[] { 0x01 }), new Jacobi.BinarySerializer.Tests.Execution.DictSink());

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.NeedMoreData));
    }

    [TestCase("UInt8", 2, 7)]
    [TestCase("UInt16", 3, 7)]
    [TestCase("Int16", 2, 8)]
    [TestCase("UInt16", 2, 9)]
    [TestCase("UInt16", 0, 7)]
    [TestCase("String", 2, 7)]
    public void Options_OutsideDataType_Throws(string type, int byteLength, int bits)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Write(Root(type, byteLength, bits), new() { ["Root.A"] = (ushort)1 }, out _));
    }
}
