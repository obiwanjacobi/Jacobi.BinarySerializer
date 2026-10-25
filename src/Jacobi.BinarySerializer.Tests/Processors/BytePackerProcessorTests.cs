using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class BytePackerProcessorTests
{
    private static SchemaGroup CreateRoot(string? endian = null)
        => Group("Root", [endian is null ? Ref("bytepacker") : Ref("bytepacker", ("byteorder", endian))],
            Field("A", "sys.uint16"),
            Field("B", "sys.uint8"),
            Field("C", "sys.int32"));

    private static Dictionary<string, object?> CreateValues()
        => new() { ["Root.A"] = (ushort)0x1234, ["Root.B"] = (byte)7, ["Root.C"] = 0x01020304 };

    [Test]
    public void RoundTrip_LittleEndian_IsTheDefault()
    {
        var (bytes, values) = RoundTrip(CreateRoot(), CreateValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x34, 0x12, 0x07, 0x04, 0x03, 0x02, 0x01 }));
        Assert.That(values, Is.EqualTo(CreateValues()));
    }

    [Test]
    public void RoundTrip_BigEndian_ReordersMultiByteValuesOnly()
    {
        var (bytes, values) = RoundTrip(CreateRoot("big"), CreateValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x12, 0x34, 0x07, 0x01, 0x02, 0x03, 0x04 }));
        Assert.That(values, Is.EqualTo(CreateValues()));
    }

    [Test]
    public void RoundTrip_NegativeAndFloatingPointValues()
    {
        var root = Group("Root", [Ref("bytepacker", ("byteorder", "big"))],
            Field("A", "sys.int64"),
            Field("B", "sys.float64"));
        var values = new Dictionary<string, object?> { ["Root.A"] = -2L, ["Root.B"] = 3.25d };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Has.Length.EqualTo(16));
        Assert.That(bytes[..8], Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_ChainedAfterHead_ConvertsFixedWidthFields()
    {
        var root = Group("Root", [Ref("align", ("bytes", "1"))],
            Field("A", "sys.uint8"),
            Group("G", [Ref("bytepacker", ("byteorder", "big"))],
                Field("B", "sys.uint16"),
                Field("C", "sys.uint8"),
                Field("D", "sys.int32")));
        var values = new Dictionary<string, object?>
        {
            ["Root.A"] = (byte)1,
            ["Root.G.B"] = (ushort)0x1234,
            ["Root.G.C"] = (byte)7,
            ["Root.G.D"] = 0x01020304
        };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0x12, 0x34, 7, 0x01, 0x02, 0x03, 0x04 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_ChainedAfterHead_LeavesOtherFieldsLittleEndian()
    {
        var root = Group("Root", [Ref("align", ("bytes", "1"))],
            Field("A", "sys.uint16"),
            Group("G", [Ref("bytepacker", ("byteorder", "big"))], Field("B", "sys.uint16")));
        var values = new Dictionary<string, object?> { ["Root.A"] = (ushort)0x1234, ["Root.G.B"] = (ushort)0x1234 };

        var (bytes, read) = RoundTrip(root, values);

        // A is read through the head only (the chained packer sits in G): little endian; B is converted.
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x34, 0x12, 0x12, 0x34 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_BitPackerOnAnOrdinaryGroup_UsesLayoutChain()
    {
        var root = Group("Root", [Ref("align", ("bytes", "1"))],
            Group("G", [Ref("bitpacker")], Field("A", "sys.uint8")));
        var values = new Dictionary<string, object?> { ["Root.G.A"] = (byte)1 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 1 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_InvalidEndian_Throws()
    {
        Assert.That(() => Write(CreateRoot("middle"), CreateValues(), out _),
            Throws.InstanceOf<Jacobi.BinarySerializer.Execution.ExecutionPlanException>());
    }
}
