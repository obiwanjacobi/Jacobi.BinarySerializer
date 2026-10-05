using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class StringProcessorTests
{
    [Test]
    public void RoundTrip_FixedLength_PadsAndTrims()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.String, [Ref("string", ("length", "6"))]),
            Field("B", SchemaDataType.UInt8));
        var values = new Dictionary<string, object?> { ["Root.A"] = "abc", ["Root.B"] = (byte)7 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0, 0, 0, 7 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_Terminated_KeepsFieldBoundaries()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.String, [Ref("string", ("terminator", "0"))]),
            Field("B", SchemaDataType.String, [Ref("string", ("terminator", "0"))]),
            Field("C", SchemaDataType.UInt8));
        var values = new Dictionary<string, object?> { ["Root.A"] = "hi", ["Root.B"] = "", ["Root.C"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'h', (byte)'i', 0, 0, 9 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_Utf8MultiByte_LengthIsInBytes()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.String, [Ref("string", ("length", "6"))]));
        var values = new Dictionary<string, object?> { ["Root.A"] = "é€" };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Has.Length.EqualTo(6));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_LongerThanTheReadWindow_GrowsTheWindow()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.String, [Ref("string", ("terminator", "0"))]),
            Field("B", SchemaDataType.String, [Ref("string", ("length", "40"))]),
            Field("C", SchemaDataType.UInt8));
        var values = new Dictionary<string, object?>
        {
            ["Root.A"] = new string('x', 100),
            ["Root.B"] = new string('y', 33),
            ["Root.C"] = (byte)1
        };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Has.Length.EqualTo(101 + 40 + 1));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_TooLong_Throws()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.String, [Ref("string", ("length", "2"))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "abc" }, out _));
    }

    [Test]
    public void Write_NoLengthOrTerminator_Throws()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.String, [Ref("string")]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "abc" }, out _));
    }

    [Test]
    public void Read_TerminatorMissing_NeedsMoreData()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.String, [Ref("string", ("terminator", "0"))]));

        var result = new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>("abc"u8.ToArray()), new DictSink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_FixedLengthTruncated_NeedsMoreData()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.String, [Ref("string", ("length", "5"))]));

        var result = new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>("abc"u8.ToArray()), new DictSink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }
}
