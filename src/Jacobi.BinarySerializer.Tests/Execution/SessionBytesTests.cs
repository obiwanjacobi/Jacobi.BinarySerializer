using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>A Bytes field holds raw bytes: its length is a constant, a reference, or the rest of the enclosing size window.</summary>
public class SessionBytesTests
{
    private static SchemaField Blob(SchemaValueOrRef<int> length = default)
        => new() { Name = "Blob", DataType = "sys.bytes", ByteLength = length };

    private static SchemaGroup LengthPrefixed()
        => Group("Root", [], Field("Len", "sys.uint8"), Blob(new SchemaNodeRef { Path = "Root.Len" }), Field("Tail", "sys.uint8"));

    [Test]
    public void Read_LengthFromReference_ReadsThatManyBytes()
    {
        var sink = new Sink();

        var result = new ReaderSession(Build(LengthPrefixed())).Read(new ReadOnlySequence<byte>(new byte[] { 3, 1, 2, 3, 9 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That((byte[])sink.Values["Root.Blob"]!, Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((byte)9));
    }

    [Test]
    public void Read_ConstantLength_ReadsThatManyBytes()
    {
        var plan = Build(Group("Root", [], Blob(2), Field("Tail", "sys.uint8")));
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 7, 8, 9 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That((byte[])sink.Values["Root.Blob"]!, Is.EqualTo(new byte[] { 7, 8 }));
    }

    [Test]
    public void Read_LengthBeyondInput_NeedsMoreData()
    {
        var result = new ReaderSession(Build(LengthPrefixed())).Read(new ReadOnlySequence<byte>(new byte[] { 5, 1, 2 }), new Sink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_NoLengthInWindow_TakesRestOfWindow()
    {
        var body = new SchemaGroup { Name = "Body", ByteSize = new SchemaNodeRef { Path = "Root.Len" }, MemberList = { Blob() } };
        var plan = Build(Group("Root", [], Field("Len", "sys.uint8"), body, Field("Tail", "sys.uint8")));
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 3, 10, 11, 12, 99 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That((byte[])sink.Values["Root.Body.Blob"]!, Is.EqualTo(new byte[] { 10, 11, 12 }));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((byte)99));
    }

    [Test]
    public void Build_NoLengthAndNoWindow_ReportsError()
        => Assert.That(() => Build(Group("Root", [], Blob())), Throws.TypeOf<ExecutionPlanException>());

    [Test]
    public void Build_NoLengthNotLast_ReportsError()
    {
        var body = new SchemaGroup { Name = "Body", ByteSize = 4, MemberList = { Blob(), Field("Tail", "sys.uint8") } };

        Assert.That(() => Build(Group("Root", [], body)), Throws.TypeOf<ExecutionPlanException>());
    }

    [Test]
    public void Build_LengthDifferentFromFixedSizeWithoutProcessor_ReportsError()
    {
        var field = new SchemaField { Name = "Number", DataType = "sys.uint8", ByteLength = 2 };

        Assert.That(() => Build(Group("Root", [], field)), Throws.TypeOf<ExecutionPlanException>());
    }

    [Test]
    public void Build_LengthEqualToFixedSize_Succeeds()
    {
        var field = new SchemaField { Name = "Number", DataType = "sys.uint16", ByteLength = 2 };

        Assert.That(() => Build(Group("Root", [], field)), Throws.Nothing);
    }

    [Test]
    public void Build_LengthOnBoolean_ReportsError()
    {
        var field = new SchemaField { Name = "Flag", DataType = "sys.boolean", ByteLength = 1 };

        Assert.That(() => Build(Group("Root", [], field)), Throws.TypeOf<ExecutionPlanException>());
    }

    [Test]
    public void Write_LengthFieldNotInModel_DerivesLength()
    {
        var plan = Build(LengthPrefixed());
        var output = new ArrayBufferWriter<byte>();
        var source = new Source(new() { ["Root.Blob"] = new byte[] { 1, 2, 3 }, ["Root.Tail"] = (byte)9 });

        Assert.That(new WriterSession(plan, output).Write(source), Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 3, 1, 2, 3, 9 }));
    }

    [Test]
    public void Write_WrongLengthInModel_Throws()
    {
        var plan = Build(LengthPrefixed());
        var source = new Source(new() { ["Root.Len"] = (byte)2, ["Root.Blob"] = new byte[] { 1, 2, 3 }, ["Root.Tail"] = (byte)9 });

        Assert.That(() => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source), Throws.Exception);
    }

    [Test]
    public void Parse_HexLiteral_ReadsBytes()
    {
        var Bytes = Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault().Get(new SchemaName("sys.bytes"));
        Assert.That(Bytes.Parse("0x89504E47", out var value), Is.True);
        Assert.That((byte[])value!, Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.That(Bytes.Parse("89 50 4E", out value), Is.True);
        Assert.That((byte[])value!, Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E }));
        Assert.That(Bytes.Parse("0x8", out _), Is.False);
        Assert.That(Bytes.Parse("0xZZ", out _), Is.False);
    }

    [Test]
    public void Read_ConstantBytes_MismatchThrows()
    {
        var signature = new SchemaField { Name = "Sig", DataType = "sys.bytes", ByteLength = 2, Value = "0x8950" };
        var plan = Build(Group("Root", [], signature));

        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0x89, 0x50 }), new Sink()), Is.EqualTo(ReadResult.Success));
        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0x89, 0x51 }), new Sink()), Throws.Exception);
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
        public void SetField(FieldContext context, LogicalField value) => Values[context.Path] = value.Value;
    }
}
