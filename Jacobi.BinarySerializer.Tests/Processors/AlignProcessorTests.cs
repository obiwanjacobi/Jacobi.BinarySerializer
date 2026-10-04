using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class AlignProcessorTests
{
    [Test]
    public void RoundTrip_AsHead_PadsFieldsAndGroupEnd()
    {
        var root = Group("Root", [Ref("align", ("bytes", "4"))],
            Field("A", SchemaDataType.UInt8),
            Field("B", SchemaDataType.UInt16));
        var values = new Dictionary<string, object?> { ["Root.A"] = (byte)1, ["Root.B"] = (ushort)0x1234 };

        var (bytes, read) = RoundTrip(root, values);

        // A at 0; B padded from 1 to 4; end padded from 6 to 8.
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0, 0, 0x34, 0x12, 0, 0 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_AlreadyAligned_AddsNoPadding()
    {
        var root = Group("Root", [Ref("align", ("bytes", "2"))],
            Field("A", SchemaDataType.UInt16),
            Field("B", SchemaDataType.UInt16));
        var values = new Dictionary<string, object?> { ["Root.A"] = (ushort)0x0102, ["Root.B"] = (ushort)0x0304 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 1, 4, 3 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_ChainedAfterBytePacker_AlignsTheReorderedBytes()
    {
        var root = Group("Root", [Ref("bytepacker", ("byteorder", "big")), Ref("align", ("bytes", "2"))],
            Field("A", SchemaDataType.UInt8),
            Field("B", SchemaDataType.UInt16),
            Field("C", SchemaDataType.UInt8));
        var values = new Dictionary<string, object?>
        {
            ["Root.A"] = (byte)1,
            ["Root.B"] = (ushort)0x1234,
            ["Root.C"] = (byte)9
        };

        var (bytes, read) = RoundTrip(root, values);

        // A at 0; B padded from 1 to 2 and big-endian; C at 4; end padded from 5 to 6.
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0x12, 0x34, 9, 0 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_GroupRelative_AlignsToTheStartOfTheOwningGroup()
    {
        var root = Group("Root", [],
            Field("P", SchemaDataType.UInt8),
            Group("G", [Ref("align", ("bytes", "4"))], Field("X", SchemaDataType.UInt8)));
        var values = new Dictionary<string, object?> { ["Root.P"] = (byte)7, ["Root.G.X"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        // G starts at 1: X is aligned already, the group end is padded from 2 to 5.
        Assert.That(bytes, Is.EqualTo(new byte[] { 7, 9, 0, 0, 0 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_RootRelative_AlignsToTheStartOfTheMessage()
    {
        var root = Group("Root", [],
            Field("P", SchemaDataType.UInt8),
            Group("G", [Ref("align", ("bytes", "4"), ("relative", "root"))], Field("X", SchemaDataType.UInt8)));
        var values = new Dictionary<string, object?> { ["Root.P"] = (byte)7, ["Root.G.X"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        // X is padded from 1 to 4, the group end from 5 to 8.
        Assert.That(bytes, Is.EqualTo(new byte[] { 7, 0, 0, 0, 9, 0, 0, 0 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Read_MissingPadding_NeedsMoreData()
    {
        var root = Group("Root", [Ref("align", ("bytes", "4"))],
            Field("A", SchemaDataType.UInt8),
            Field("B", SchemaDataType.UInt8));
        var plan = Build(root);

        var result = new Jacobi.BinarySerializer.Execution.ReaderSession(plan)
            .Read(new System.Buffers.ReadOnlySequence<byte>([1, 0]), new Jacobi.BinarySerializer.Tests.Execution.DictSink());

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.NeedMoreData));
    }

    [Test]
    public void Write_MissingBytesProperty_Throws()
    {
        var root = Group("Root", [Ref("align")], Field("A", SchemaDataType.UInt8));

        Assert.That(() => Write(root, new() { ["Root.A"] = (byte)1 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [TestCase("0")]
    [TestCase("-4")]
    [TestCase("many")]
    public void Write_InvalidBytes_Throws(string bytes)
    {
        var root = Group("Root", [Ref("align", ("bytes", bytes))], Field("A", SchemaDataType.UInt8));

        Assert.That(() => Write(root, new() { ["Root.A"] = (byte)1 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Write_InvalidRelative_Throws()
    {
        var root = Group("Root", [Ref("align", ("bytes", "4"), ("relative", "sideways"))], Field("A", SchemaDataType.UInt8));

        Assert.That(() => Write(root, new() { ["Root.A"] = (byte)1 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
