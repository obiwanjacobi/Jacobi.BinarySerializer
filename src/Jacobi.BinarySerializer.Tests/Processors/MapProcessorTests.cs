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
            Field("Color", "sys.int32", [Ref("map", ("1", "Red"), ("2", "Green"), ("4", "Blue"))]));

    private static SchemaGroup StringRoot()
        => Group("Root", [],
            Field("Type", "sys.string", [
                Ref("map", ("to", "sys.int32"), ("IHDR", "0"), ("PLTE", "1")),
                Ref("string", ("byteLength", "4"), ("encoding", "ascii"))]));

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
    public void Write_InvalidKeyForSourceType_Throws()
    {
        var root = Group("Root", [],
            Field("A", "sys.int32", [Ref("map", ("x", "1"))]));

        Assert.That(() => Write(root, new() { ["Root.A"] = 1 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    private static SchemaGroup StringRootWithDefault()
    {
        var map = Ref("map", ("to", "sys.int32"), ("IHDR", "0"), ("default", "4"));
        return Group("Root", [],
            Field("Type", "sys.string", [map, Ref("string", ("byteLength", "4"), ("encoding", "ascii"))]));
    }

    [Test]
    public void Read_UnmappedValue_ReturnsTheDefault()
    {
        var plan = Build(StringRootWithDefault());
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>("ABCD"u8.ToArray()), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(Convert.ToInt32(sink.Values["Root.Type"]), Is.EqualTo(4));
    }

    [Test]
    public void Read_MappedValue_IgnoresTheDefault()
    {
        var plan = Build(StringRootWithDefault());
        var sink = new DictSink();

        new ReaderSession(plan).Read(new ReadOnlySequence<byte>("IHDR"u8.ToArray()), sink);

        Assert.That(Convert.ToInt32(sink.Values["Root.Type"]), Is.EqualTo(0));
    }

    [Test]
    public void Write_TheDefaultValue_Throws()
    {
        Assert.That(() => Write(StringRootWithDefault(), new() { ["Root.Type"] = 4 }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Read_UnmappedValueWithoutDefault_Fails()
    {
        var plan = Build(StringRoot());
        var sink = new DictSink();

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>("ABCD"u8.ToArray()), sink),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
