using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class BytePackerProcessorTests
{
    private static SchemaGroup CreateRoot(string? endian = null)
        => Group("Root", [endian is null ? Ref("bytepacker") : Ref("bytepacker", ("byteorder", endian))],
            Field("A", "UInt16"),
            Field("B", "UInt8"),
            Field("C", "Int32"));

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
            Field("A", "Int64"),
            Field("B", "Double"));
        var values = new Dictionary<string, object?> { ["Root.A"] = -2L, ["Root.B"] = 3.25d };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Has.Length.EqualTo(16));
        Assert.That(bytes[..8], Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_InvalidEndian_Throws()
    {
        Assert.That(() => Write(CreateRoot("middle"), CreateValues(), out _),
            Throws.InstanceOf<Jacobi.BinarySerializer.Execution.ExecutionPlanException>());
    }
}
