using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>Writes a value model with a WriterSession and reads it back with a ReaderSession over the same plan.</summary>
public class SessionRoundTripTests
{
    [TestCase("sys.int8", (sbyte)-5)]
    [TestCase("sys.uint8", (byte)200)]
    [TestCase("sys.int16", (short)-1234)]
    [TestCase("sys.uint16", (ushort)65000)]
    [TestCase("sys.int32", -123456)]
    [TestCase("sys.uint32", 4000000000u)]
    [TestCase("sys.int64", long.MinValue)]
    [TestCase("sys.uint64", ulong.MaxValue)]
    [TestCase("sys.double", 3.14159d)]
    [TestCase("sys.boolean", true)]
    public void RoundTrip_FixedWidthTypes_PreserveValueAndClrType(string type, object value)
    {
        var root = Group("Root", [], Field("A", new SchemaName(type)));

        var sink = RoundTrip(root, new() { ["Root.A"] = value });

        Assert.That(sink.Values["Root.A"], Is.EqualTo(value));
        Assert.That(sink.Values["Root.A"], Is.TypeOf(value.GetType()));
    }

    [Test]
    public void RoundTrip_NestedGroups_PreserveValues()
    {
        var root = Group("Root", [],
            Field("A"),
            Group("G", [], Field("B", "sys.uint8"), Group("H", [], Field("C", "sys.int16"))),
            Field("D"));
        var values = new Dictionary<string, object?>
        {
            ["Root.A"] = 1,
            ["Root.G.B"] = (byte)2,
            ["Root.G.H.C"] = (short)-3,
            ["Root.D"] = 4,
        };

        var sink = RoundTrip(root, values);

        Assert.That(sink.Values, Is.EqualTo(values));
        Assert.That(sink.Events, Is.EqualTo(new[]
        {
            "Enter:Root.G", "Enter:Root.G.H", "Complete:Root.G.H", "Complete:Root.G", "Complete:Root"
        }));
    }

    [Test]
    public void RoundTrip_AllCustomProcessors_PreserveValues()
    {
        var root = Group("Root", [Ref("layout"), Ref("stream")],
            FieldWith("A", Ref("scale")),
            FieldWith("B", Ref("bigendian")),
            Group("G", [], Field("C")));
        var values = new Dictionary<string, object?> { ["Root.A"] = 21, ["Root.B"] = 258, ["Root.G.C"] = 7 };

        var sink = RoundTrip(root, values);

        Assert.That(sink.Values, Is.EqualTo(values));
    }

    [Test]
    public void RoundTrip_ProcessorsTransformTheWireFormat()
    {
        var root = Group("Root", [Ref("stream")], FieldWith("A", Ref("scale")), FieldWith("B", Ref("bigendian")));
        var plan = Build(root);
        var output = new ArrayBufferWriter<byte>();

        new WriterSession(plan, output).Write(new DictSource(new() { ["Root.A"] = 5, ["Root.B"] = 1 }));

        // frame byte, 5 scaled to 10 (little-endian), 1 big-endian
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0xFF, 10, 0, 0, 0, 0, 0, 0, 1 }));
    }

    [Test]
    public void RoundTrip_TruncatedInput_NeedsMoreDataThenSucceeds()
    {
        var root = Group("Root", [], Field("A"), Field("B"));
        var plan = Build(root);
        var output = new ArrayBufferWriter<byte>();
        new WriterSession(plan, output).Write(new DictSource(new() { ["Root.A"] = 1, ["Root.B"] = 2 }));
        var bytes = output.WrittenMemory;

        var partial = new DictSink();
        var first = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes[..6]), partial);
        var complete = new DictSink();
        var second = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), complete);

        Assert.That(first, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(second, Is.EqualTo(ReadResult.Success));
        Assert.That(complete.Values, Is.EqualTo(new Dictionary<string, object?> { ["Root.A"] = 1, ["Root.B"] = 2 }));
    }

    [Test]
    public void RoundTrip_SessionsAreReusableForASecondMessage()
    {
        var root = Group("Root", [Ref("layout")], FieldWith("A", Ref("scale")));
        var plan = Build(root);

        foreach (var value in new[] { 3, 9 })
        {
            var output = new ArrayBufferWriter<byte>();
            new WriterSession(plan, output).Write(new DictSource(new() { ["Root.A"] = value }));
            var sink = new DictSink();

            new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);

            Assert.That(sink.Values["Root.A"], Is.EqualTo(value));
        }
    }

    private static DictSink RoundTrip(SchemaGroup root, Dictionary<string, object?> values)
    {
        var plan = Build(root);
        var output = new ArrayBufferWriter<byte>();
        var written = new WriterSession(plan, output).Write(new DictSource(values));
        Assert.That(written, Is.EqualTo(WriteResult.Success));

        var sink = new DictSink();
        var read = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);
        Assert.That(read, Is.EqualTo(ReadResult.Success));
        return sink;
    }
}
