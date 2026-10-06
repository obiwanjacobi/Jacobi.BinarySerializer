using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class MapProcessorTests
{
    private static SchemaGroup IntRoot()
        => Group("Root", [],
            Field("Color", SchemaDataType.Int32, [Ref("map", ("Red", "1"), ("Green", "2"), ("Blue", "4"))]));

    private static SchemaGroup StringRoot()
        => Group("Root", [],
            Field("Type", SchemaDataType.String, [
                Ref("map", ("logical", "Int32"), ("0", "IHDR"), ("1", "PLTE")),
                Ref("string", ("length", "4"), ("encoding", "ascii"))]));

    [TestCase("Red", 1)]
    [TestCase("Blue", 4)]
    public void RoundTrip_StringToInt_WritesPhysicalNumber(string option, int number)
    {
        var (bytes, values) = RoundTrip(IntRoot(), new() { ["Root.Color"] = option });

        Assert.That(values["Root.Color"], Is.EqualTo(option));
        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(number)));
    }

    [TestCase(0, "IHDR")]
    [TestCase(1, "PLTE")]
    public void RoundTrip_IntToString_WritesPhysicalText(int index, string text)
    {
        var (bytes, values) = RoundTrip(StringRoot(), new() { ["Root.Type"] = index });

        Assert.That(Convert.ToInt32(values["Root.Type"]), Is.EqualTo(index));
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes), Is.EqualTo(text));
    }

    [Test]
    public void Write_UnmappedValue_Throws()
    {
        Assert.That(() => Write(IntRoot(), new() { ["Root.Color"] = "Purple" }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Write_InvalidKeyForLogicalType_Throws()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.Int32, [Ref("map", ("logical", "Int32"), ("x", "1"))]));

        Assert.That(() => Write(root, new() { ["Root.A"] = 1 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    private static SchemaGroup StringRootWithDefault()
    {
        var map = Ref("map", ("logical", "Int32"), ("0", "IHDR"));
        map.PropertyList.Add(new SchemaProperty { Name = new ProcessorKey("sys", "map").PropertyName("4"), Value = null });
        return Group("Root", [],
            Field("Type", SchemaDataType.String, [map, Ref("string", ("length", "4"), ("encoding", "ascii"))]));
    }

    [Test]
    public void Read_UnmappedPhysicalValue_ReturnsTheDefaultsLogicalValue()
    {
        var plan = Build(StringRootWithDefault());
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>("ABCD"u8.ToArray()), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(Convert.ToInt32(sink.Values["Root.Type"]), Is.EqualTo(4));
    }

    [Test]
    public void Read_MappedPhysicalValue_IgnoresTheDefault()
    {
        var plan = Build(StringRootWithDefault());
        var sink = new DictSink();

        new ReaderSession(plan).Read(new ReadOnlySequence<byte>("IHDR"u8.ToArray()), sink);

        Assert.That(Convert.ToInt32(sink.Values["Root.Type"]), Is.EqualTo(0));
    }

    [Test]
    public void Write_DefaultsLogicalValue_Throws()
    {
        Assert.That(() => Write(StringRootWithDefault(), new() { ["Root.Type"] = 4 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
