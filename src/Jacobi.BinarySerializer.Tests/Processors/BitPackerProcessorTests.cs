using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class BitPackerProcessorTests
{
    private static SchemaGroup CreateRoot(string? bitOrder = null)
        => Group("Root", [bitOrder is null ? Ref("bitpacker") : Ref("bitpacker", ("bitorder", bitOrder))],
            Field("A", SchemaDataType.UInt8, null, ("bits", "3")),
            Field("B", SchemaDataType.UInt8, null, ("bits", "5")),
            Field("C", SchemaDataType.UInt16, null, ("bits", "13")));

    private static Dictionary<string, object?> CreateValues()
        => new() { ["Root.A"] = (byte)5, ["Root.B"] = (byte)17, ["Root.C"] = (ushort)0x1234 };

    [Test]
    public void RoundTrip_LsbFirst_IsTheDefault()
    {
        var (bytes, values) = RoundTrip(CreateRoot(), CreateValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x8D, 0x34, 0x12 }));
        Assert.That(values, Is.EqualTo(CreateValues()));
    }

    [Test]
    public void RoundTrip_MsbFirst()
    {
        var (bytes, values) = RoundTrip(CreateRoot("big"), CreateValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xB1, 0x91, 0xA0 }));
        Assert.That(values, Is.EqualTo(CreateValues()));
    }

    [TestCase("little")]
    [TestCase("big")]
    public void RoundTrip_SignedValuesAreSignExtended(string bitOrder)
    {
        var root = Group("Root", [Ref("bitpacker", ("bitorder", bitOrder))],
            Field("A", SchemaDataType.Int8, null, ("bits", "4")),
            Field("B", SchemaDataType.Int16, null, ("bits", "12")),
            Field("C", SchemaDataType.Boolean, null, ("bits", "1")));
        var values = new Dictionary<string, object?> { ["Root.A"] = (sbyte)-3, ["Root.B"] = (short)-1000, ["Root.C"] = true };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Has.Length.EqualTo(3));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_EachGroupStartsOnAByteBoundary()
    {
        var root = Group("Root", [],
            Group("First", [Ref("bitpacker")], Field("A", SchemaDataType.UInt8, null, ("bits", "3"))),
            Group("Second", [Ref("bitpacker")], Field("B", SchemaDataType.UInt8, null, ("bits", "3"))));
        var values = new Dictionary<string, object?> { ["Root.First.A"] = (byte)7, ["Root.Second.B"] = (byte)5 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x07, 0x05 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_ValueWiderThanTheBits_Fails()
    {
        Write(CreateRoot(), new() { ["Root.A"] = (byte)9, ["Root.B"] = (byte)0, ["Root.C"] = (ushort)0 }, out var result);

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    [Test]
    public void Write_InvalidBitOrder_Throws()
    {
        Assert.That(() => Write(CreateRoot("sideways"), CreateValues(), out _),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
