using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class BitsProcessorTests
{
    private static SchemaGroup Root(string type, int offset, int length)
        => Group("Root", [], Field("A", type, [Ref("bits", ("bitoffset", offset.ToString()), ("bitlength", length.ToString()))]));

    private static Dictionary<string, object?> Read(SchemaGroup root, params byte[] bytes)
    {
        var sink = new DictSink();
        var result = new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>(bytes), sink);
        Assert.That(result, Is.EqualTo(ReadResult.Success));
        return sink.Values;
    }

    [Test]
    public void Read_ExtractsBitRange()
    {
        var values = Read(Root("UInt8", 4, 3), 0b1101_0110);

        Assert.That(values["Root.A"], Is.EqualTo((byte)0b101));
    }

    [Test]
    public void Write_PlacesBitRange_OtherBitsZero()
    {
        var bytes = Write(Root("UInt8", 4, 3), new() { ["Root.A"] = (byte)0b101 }, out var result);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(bytes, Is.EqualTo(new byte[] { 0b0101_0000 }));
    }

    [Test]
    public void RoundTrip_UInt16_HighBits()
    {
        var values = new Dictionary<string, object?> { ["Root.A"] = (ushort)0x2A };

        var (bytes, read) = RoundTrip(Root("UInt16", 8, 8), values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2A, 0x00 }).Or.EqualTo(new byte[] { 0x00, 0x2A }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Read_SignedRange_IsSignExtended()
    {
        var values = Read(Root("Int8", 2, 4), 0b0011_1000);

        Assert.That(values["Root.A"], Is.EqualTo((sbyte)-2));
    }

    [Test]
    public void RoundTrip_SignedRange()
    {
        var values = new Dictionary<string, object?> { ["Root.A"] = (sbyte)-2 };

        var (bytes, read) = RoundTrip(Root("Int8", 2, 4), values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0b0011_1000 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_ValueDoesNotFit_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Write(Root("UInt8", 0, 3), new() { ["Root.A"] = (byte)8 }, out _));
    }

    [TestCase(6, 3)]
    [TestCase(0, 9)]
    [TestCase(8, 1)]
    [TestCase(-1, 2)]
    [TestCase(0, 0)]
    public void Range_OutsideDataType_Throws(int offset, int length)
    {
        Assert.Throws<InvalidOperationException>(() => Read(Root("UInt8", offset, length), 0xFF));
    }

    [Test]
    public void Read_NonIntegerType_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Read(Root("String", 0, 1), 0xFF));
    }

    [Test]
    public void Read_VirtualFieldPeeksBitRange()
    {
        var field = new SchemaField
        {
            Name = "Kind",
            DataType = "UInt8",
            ByteOffset = 0,
            ProcessorsList = [Ref("bits", ("bitoffset", "4"), ("bitlength", "4"))]
        };
        var root = Group("Root", [], field, Field("A", "UInt8"));

        var values = Read(root, 0xA5);

        Assert.That(values["Root.Kind"], Is.EqualTo((byte)0xA));
        Assert.That(values["Root.A"], Is.EqualTo((byte)0xA5));
    }
}
