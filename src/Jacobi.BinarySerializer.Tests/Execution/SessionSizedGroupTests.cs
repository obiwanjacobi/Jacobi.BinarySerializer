using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>A group with a size is limited to that many encoded bytes.</summary>
public class SessionSizedGroupTests
{
    private static SchemaGroup Body()
        => new()
        {
            Name = "Body",
            ByteSize = new SchemaNodeRef { Path = "Root.Len" },
            MemberList = { Field("A", "sys.int16"), Field("B", "sys.int16") },
        };

    private static SchemaGroup Root()
        => Group("Root", [], Field("Len", "sys.uint16"), Body());

    [Test]
    public void Write_SizeFieldNotInModel_DerivesSize()
    {
        var plan = Build(Root());
        var output = new ArrayBufferWriter<byte>();
        var source = new Source(new() { ["Root.Body.A"] = (short)1, ["Root.Body.B"] = (short)2 });

        Assert.That(new WriterSession(plan, output).Write(source), Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenCount, Is.EqualTo(6));

        var sink = new Sink();
        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.Len"], Is.EqualTo((ushort)4));
        Assert.That(sink.Values["Root.Body.B"], Is.EqualTo((short)2));
    }

    [Test]
    public void Write_WrongSizeInModel_Throws()
    {
        var plan = Build(Root());
        var source = new Source(new() { ["Root.Len"] = (ushort)3, ["Root.Body.A"] = (short)1, ["Root.Body.B"] = (short)2 });

        Assert.That(() => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source), Throws.Exception);
    }

    [Test]
    public void Read_SizeLargerThanContent_Throws()
    {
        var plan = Build(Root());
        byte[] bytes = [6, 0, 1, 0, 2, 0, 0, 0];

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new Sink()), Throws.Exception);
    }

    [Test]
    public void Read_SizeSmallerThanContent_Throws()
    {
        var plan = Build(Root());
        byte[] bytes = [2, 0, 1, 0, 2, 0];

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new Sink()), Throws.Exception);
    }

    [Test]
    public void Read_SizeBeyondInput_NeedsMoreData()
    {
        var plan = Build(Root());
        byte[] bytes = [9, 0, 1, 0];

        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new Sink()), Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_OpenRepeatInWindow_EndsAtWindowEnd()
    {
        var items = new SchemaRepeat { Name = "Items", MemberList = { Field("Byte", "sys.uint8") } };
        var body = new SchemaGroup { Name = "Body", ByteSize = new SchemaNodeRef { Path = "Root.Len" }, MemberList = { items } };
        var plan = Build(Group("Root", [], Field("Len", "sys.uint8"), body, Field("Tail", "sys.uint8")));
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 3, 10, 11, 12, 99 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Calls, Is.EqualTo(5));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((byte)99));
    }

    [Test]
    public void Read_SizedRepeatWithoutCount_EndsAtWindowEnd()
    {
        var items = new SchemaRepeat { Name = "Items", ByteSize = new SchemaNodeRef { Path = "Root.Len" }, MemberList = { Field("Byte", "sys.uint8") } };
        var plan = Build(Group("Root", [], Field("Len", "sys.uint8"), items, Field("Tail", "sys.uint8")));
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 3, 10, 11, 12, 99 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Calls, Is.EqualTo(5));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((byte)99));
    }

    [Test]
    public void Read_SizedRepeatWithoutCount_ZeroSize_ReadsNoItems()
    {
        var items = new SchemaRepeat { Name = "Items", ByteSize = new SchemaNodeRef { Path = "Root.Len" }, MemberList = { Field("Byte", "sys.uint8") } };
        var plan = Build(Group("Root", [], Field("Len", "sys.uint8"), items, Field("Tail", "sys.uint8")));
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0, 99 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Calls, Is.EqualTo(2));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((byte)99));
    }

    [Test]
    public void Write_SizedRepeatWithoutCount_DerivesSize()
    {
        var items = new SchemaRepeat { Name = "Items", ByteSize = new SchemaNodeRef { Path = "Root.Len" }, MemberList = { Field("Byte", "sys.uint8") } };
        var plan = Build(Group("Root", [], Field("Len", "sys.uint8"), items, Field("Tail", "sys.uint8")));
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(new ItemsSource(3));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 3, 1, 2, 3, 99 }));
    }

    private sealed class ItemsSource(int items) : IFieldSource
    {
        public SourceResult GetField(FieldContext context)
        {
            if (context.Path.ToString() == "Root.Tail")
            {
                return SourceResult.Provided(new LogicalField("Tail", typeof(byte), (byte)99));
            }
            if (context.Path.ToString() == "Root.Items.Byte")
            {
                var index = context.Instance.ToString().Trim('[', ']').Split('[', ']').Where(s => s.Length > 0).Select(Int32.Parse).Last();
                if (index >= items)
                {
                    return SourceResult.EndOfData();
                }
                return SourceResult.Provided(new LogicalField("Byte", typeof(byte), (byte)(index + 1)));
            }
            return SourceResult.NoValue();
        }
    }

    private sealed class Source(Dictionary<string, object?> values) : IFieldSource
    {
        public SourceResult GetField(FieldContext context) => TryGetField(context, out var found) ? SourceResult.Provided(found) : SourceResult.NoValue();
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
        public int Calls { get; private set; }
        public void SetField(FieldContext context, LogicalField value)
        {
            Calls++;
            Values[context.Path] = value.Value;
        }
    }
}
