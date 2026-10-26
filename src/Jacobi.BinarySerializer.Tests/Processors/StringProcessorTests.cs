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
            Field("A", "sys.string", [Ref("string", ("byteLength", "6"))]),
            Field("B", "sys.uint8"));
        var values = new Dictionary<string, object?> { ["Root.A"] = "abc", ["Root.B"] = (byte)7 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0, 0, 0, 7 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_Terminated_KeepsFieldBoundaries()
    {
        var root = Group("Root", [],
            Field("A", "sys.string", [Ref("string", ("terminator", "0"))]),
            Field("B", "sys.string", [Ref("string", ("terminator", "0"))]),
            Field("C", "sys.uint8"));
        var values = new Dictionary<string, object?> { ["Root.A"] = "hi", ["Root.B"] = "", ["Root.C"] = (byte)9 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'h', (byte)'i', 0, 0, 9 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_Utf8MultiByte_LengthIsInBytes()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "6"))]));
        var values = new Dictionary<string, object?> { ["Root.A"] = "é€" };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Has.Length.EqualTo(6));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_LongerThanTheReadWindow_GrowsTheWindow()
    {
        var root = Group("Root", [],
            Field("A", "sys.string", [Ref("string", ("terminator", "0"))]),
            Field("B", "sys.string", [Ref("string", ("byteLength", "40"))]),
            Field("C", "sys.uint8"));
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
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "2"))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "abc" }, out _));
    }

    [Test]
    public void Write_NoLengthOrTerminator_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string")]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "abc" }, out _));
    }

    [Test]
    public void Read_TerminatorMissing_NeedsMoreData()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("terminator", "0"))]));

        var result = new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>("abc"u8.ToArray()), new DictSink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_FixedLengthTruncated_NeedsMoreData()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "5"))]));

        var result = new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>("abc"u8.ToArray()), new DictSink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void RoundTrip_FieldLength_PadsAndTrims()
    {
        var a = new SchemaField { Name = "A", DataType = "sys.string", ByteLength = 6, ProcessorsList = [Ref("string")] };
        var root = Group("Root", [], a, Field("B", "sys.uint8"));
        var values = new Dictionary<string, object?> { ["Root.A"] = "abc", ["Root.B"] = (byte)7 };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'a', (byte)'b', (byte)'c', 0, 0, 0, 7 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_CharacterMap_ReplacesTheMappedBytes()
    {
        var root = Group("Root", [],
            Field("A", "sys.string", [Ref("string", ("byteLength", "6"), ("encoding", "ascii"), ("0x7E", "→"), ("22", "‼"), ("0x01", "𝄞"))]));
        var values = new Dictionary<string, object?> { ["Root.A"] = "A→‼𝄞b" };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'A', 0x7E, 0x16, 0x01, (byte)'b', 0 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_CharacterMap_CharacterOfAMappedByte_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "2"), ("encoding", "ascii"), ("0x7E", "→"))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "~" }, out _));
    }

    [Test]
    public void Write_NotEncodable_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "2"), ("encoding", "ascii"), ("0x7E", "→"))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "é" }, out _));
    }

    [TestCase("utf-8", "0x7E", "→")]     // not a single-byte encoding
    [TestCase("ascii", "abc", "→")]      // not a byte value
    [TestCase("ascii", "0x100", "→")]    // not a byte value
    [TestCase("ascii", "0x7E", "ab")]    // not one text element
    public void Write_InvalidCharacterMap_Throws(string encoding, string name, string value)
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "2"), ("encoding", encoding), (name, value))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "a" }, out _));
    }

    [Test]
    public void Write_CharacterMap_TextMappedTwice_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "2"), ("encoding", "ascii"), ("0x7E", "→"), ("0x7F", "→"))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "a" }, out _));
    }

    [Test]
    public void RoundTrip_ValidBytes_PaddingInRange()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "4"), ("encoding", "ascii"), ("padding", "32"), ("validBytes", "0x16-0x7F"))]));
        var values = new Dictionary<string, object?> { ["Root.A"] = "ab" };

        var (bytes, read) = RoundTrip(root, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { (byte)'a', (byte)'b', 0x20, 0x20 }));
        Assert.That(read, Is.EqualTo(values));
    }

    [Test]
    public void Write_ValidBytes_PaddingOutOfRange_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "4"), ("encoding", "ascii"), ("validBytes", "0x16-0x7F"))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "ab" }, out _));
    }

    [Test]
    public void Read_ValidBytes_ByteOutOfRange_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("terminator", "0"), ("encoding", "ascii"), ("validBytes", "0x16-0x7F, 0x0A"))]));

        Assert.That(new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>(new byte[] { 0x41, 0x0A, 0x42, 0 }), new DictSink()),
            Is.EqualTo(ReadResult.Success));
        Assert.That(() => new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>(new byte[] { 0x41, 0x05, 0 }), new DictSink()),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [TestCase("0x16-")]
    [TestCase("0x7F-0x16")]
    [TestCase("256")]
    public void Write_InvalidValidBytes_Throws(string validBytes)
    {
        var root = Group("Root", [], Field("A", "sys.string", [Ref("string", ("byteLength", "2"), ("validBytes", validBytes))]));

        Assert.Throws<InvalidOperationException>(() => Write(root, new() { ["Root.A"] = "a" }, out _));
    }

    [Test]
    public void Write_FieldLengthAndLengthProperty_Throws()
    {
        var a = new SchemaField { Name = "A", DataType = "sys.string", ByteLength = 6, ProcessorsList = [Ref("string", ("byteLength", "6"))] };
        var root = Group("Root", [], a);

        Assert.That(() => Write(root, new() { ["Root.A"] = "abc" }, out _), Throws.InstanceOf<Exception>());
    }
}
