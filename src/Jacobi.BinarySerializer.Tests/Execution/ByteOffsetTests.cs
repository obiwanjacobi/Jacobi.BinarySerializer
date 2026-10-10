using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>
/// A field with a byte offset is virtual: read at the offset, position restored, not known to the model.
/// </summary>
public class ByteOffsetTests
{
    private static SchemaField Virtual(string name, int byteOffset)
        => new() { Name = name, DataType = "sys.uint8", ByteOffset = byteOffset };

    private static SchemaField Plain(string name)
        => new() { Name = name, DataType = "sys.uint8" };

    private static (ReadResult Result, Sink Sink) Read(SchemaGroup root, params byte[] bytes)
    {
        var sink = new Sink();
        var result = new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>(bytes), sink);
        return (result, sink);
    }

    [Test]
    public void Read_ForwardOffset_RestoresPositionAndGivesValueToModel()
    {
        var root = Group("Root", [], Virtual("Peek", 1), Plain("A"), Plain("B"));

        var (result, sink) = Read(root, 10, 20);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Has.Count.EqualTo(3));
        Assert.That(sink.Values["Root.Peek"], Is.EqualTo((byte)20));
        Assert.That(sink.Values["Root.A"], Is.EqualTo((byte)10));
        Assert.That(sink.Values["Root.B"], Is.EqualTo((byte)20));
    }

    [Test]
    public void Read_BackwardOffset_ReadsEarlierByte()
    {
        var root = Group("Root", [], Plain("A"), Plain("B"), Virtual("Back", -2), Plain("C"));

        var (result, sink) = Read(root, 10, 20, 30);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Has.Count.EqualTo(4));
        Assert.That(sink.Values["Root.Back"], Is.EqualTo((byte)10));
        Assert.That(sink.Values["Root.C"], Is.EqualTo((byte)30));
    }

    [Test]
    public void Read_OffsetPastEnd_ReturnsNeedMoreData()
    {
        var root = Group("Root", [], Virtual("Peek", 5), Plain("A"));

        var (result, _) = Read(root, 1, 2);

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_OffsetBeforeStart_Throws()
    {
        var root = Group("Root", [], Virtual("Peek", -1), Plain("A"));

        Assert.Throws<InvalidOperationException>(() => Read(root, 1, 2));
    }

    [Test]
    public void Read_OffsetField_DrivesChoice()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = new SchemaNodeRef { Path = "Root.Kind" } };
        choice.MemberList.Add(Field("A", "sys.int16"));
        choice.MemberList.Add(Field("B", "sys.int16"));
        var root = Group("Root", [], Virtual("Kind", 1), choice);

        var (result, sink) = Read(root, 0, 1);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Has.Count.EqualTo(2));
        Assert.That(sink.Values["Root.Pick.B"], Is.EqualTo((short)256));
    }

    [Test]
    public void Write_OffsetField_WritesNoBytes()
    {
        var root = Group("Root", [], Virtual("Peek", 1), Plain("A"));
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(Build(root), output)
            .Write(new Source(new() { ["Root.Peek"] = (byte)9, ["Root.A"] = (byte)5 }));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5 }));
    }

    [Test]
    public void Write_OffsetFieldWithoutValue_Throws()
    {
        var root = Group("Root", [], Virtual("Peek", 1), Plain("A"));

        Assert.Throws<InvalidOperationException>(() =>
            new WriterSession(Build(root), new ArrayBufferWriter<byte>()).Write(new Source(new() { ["Root.A"] = (byte)5 })));
    }

    [Test]
    public void Write_OffsetField_DrivesChoice()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = new SchemaNodeRef { Path = "Root.Kind" } };
        choice.MemberList.Add(Field("A", "sys.int16"));
        choice.MemberList.Add(Field("B", "sys.int16"));
        var root = Group("Root", [], Virtual("Kind", 0), choice);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(Build(root), output)
            .Write(new Source(new() { ["Root.Kind"] = (byte)1, ["Root.Pick.B"] = (short)7 }));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 7, 0 }));
    }

    private sealed class Source(Dictionary<string, object?> values) : IFieldSource
    {
        public SourceResult GetField(FieldContext context)
            => TryGetField(context, out var found) ? SourceResult.Provided(found) : SourceResult.NoValue();

        private bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
        {
            if (values.TryGetValue(context.Path, out var value))
            {
                field = new LogicalField(context.Name, value!.GetType(), value);
                return true;
            }
            field = null;
            return false;
        }
    }

    private sealed class Sink : IFieldSink
    {
        public Dictionary<string, object?> Values { get; } = [];
        public void SetField(FieldContext context, LogicalField value) => Values[context.Path] = value.Value;
    }
}
