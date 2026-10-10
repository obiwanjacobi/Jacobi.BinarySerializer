using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>
/// A field with a <c>Value</c> must have that value: a constant, a published value or another field's value.
/// </summary>
public class SessionFieldValueTests
{
    private static SchemaField Valued(string name, SchemaValueOrRef<string> value)
        => new() { Name = name, DataType = "sys.uint8", Value = value };

    private static ReadResult Read(ExecutionPlan plan, byte[] bytes, out DictSink sink, ReaderSession? session = null)
    {
        sink = new DictSink();
        return (session ?? new ReaderSession(plan)).Read(new ReadOnlySequence<byte>(bytes), sink);
    }

    private static byte[] WriteBytes(WriterSession session, ArrayBufferWriter<byte> output, Dictionary<string, object?> values)
    {
        Assert.That(session.Write(new DictSource(values)), Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    [Test]
    public void Build_InvalidConstant_ReportsError()
        => Assert.That(() => Build(Group("Root", [], Valued("Magic", "abc"))), Throws.TypeOf<ExecutionPlanException>());

    [Test]
    public void Read_ConstantMatches_Succeeds()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A")));

        Assert.That(Read(plan, [0x2A], out var sink), Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values.ContainsKey("Root.Magic"), Is.False);
    }

    [Test]
    public void Read_ConstantMatches_WithValueFieldsUseModel_EmitsTheValue()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A")));

        Assert.That(Read(plan, [0x2A], out var sink, new ReaderSession(plan, valueFieldsUseModel: true)), Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.Magic"], Is.EqualTo((byte)0x2A));
    }

    [Test]
    public void Read_ConstantDecimal_Succeeds()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "42")));

        Assert.That(Read(plan, [42], out _), Is.EqualTo(ReadResult.Success));
    }

    [Test]
    public void Read_ConstantMismatch_Throws()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A")));

        Assert.That(() => Read(plan, [0x2B], out _), Throws.Exception);
    }

    [Test]
    public void Write_ConstantNotInModel_WritesConstant()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A"), Field("B", "sys.uint8")));
        var output = new ArrayBufferWriter<byte>();

        var bytes = WriteBytes(new WriterSession(plan, output), output, new() { ["Root.B"] = (byte)1 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2A, 1 }));
    }

    [Test]
    public void Write_ConstantMatchesModel_Succeeds()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A")));
        var output = new ArrayBufferWriter<byte>();

        var bytes = WriteBytes(new WriterSession(plan, output), output, new() { ["Root.Magic"] = (byte)0x2A });

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2A }));
    }

    [Test]
    public void Write_ConstantDiffersFromModel_ConstantWins()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A")));
        var output = new ArrayBufferWriter<byte>();

        var bytes = WriteBytes(new WriterSession(plan, output), output, new() { ["Root.Magic"] = (byte)1 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2A }));
    }

    [Test]
    public void Write_ConstantMismatchesModel_WithValueFieldsUseModel_Throws()
    {
        var plan = Build(Group("Root", [], Valued("Magic", "0x2A")));
        var session = new WriterSession(plan, new ArrayBufferWriter<byte>(), valueFieldsUseModel: true);

        Assert.That(() => session.Write(new DictSource(new() { ["Root.Magic"] = (byte)1 })), Throws.Exception);
    }

    [Test]
    public void Write_PublishedValue_WritesThePublishedValue()
    {
        var plan = Build(Group("Root", [], Valued("Magic", new SchemaPubRef { Namespace = "hdr", Name = "magic" })));
        var output = new ArrayBufferWriter<byte>();
        var session = new WriterSession(plan, output);
        session.Publish("hdr", "magic", 7);

        Assert.That(WriteBytes(session, output, []), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public void Write_PublishedValueNotPublished_Throws()
    {
        var plan = Build(Group("Root", [], Valued("Magic", new SchemaPubRef { Namespace = "hdr", Name = "magic" })));
        var session = new WriterSession(plan, new ArrayBufferWriter<byte>());

        Assert.That(() => session.Write(new DictSource([])), Throws.Exception);
    }

    [Test]
    public void Read_PublishedValue_MatchAndMismatch()
    {
        var plan = Build(Group("Root", [], Valued("Magic", new SchemaPubRef { Namespace = "hdr", Name = "magic" })));

        var ok = new ReaderSession(plan);
        ok.Publish("hdr", "magic", 7);
        Assert.That(Read(plan, [7], out _, ok), Is.EqualTo(ReadResult.Success));

        var bad = new ReaderSession(plan);
        bad.Publish("hdr", "magic", 7);
        Assert.That(() => Read(plan, [8], out _, bad), Throws.Exception);
    }

    [Test]
    public void Read_NodeRefValue_MatchAndMismatch()
    {
        var plan = Build(Group("Root", [], Field("A", "sys.uint8"), Valued("Copy", new SchemaNodeRef { Path = "Root.A" })));

        Assert.That(Read(plan, [5, 5], out _), Is.EqualTo(ReadResult.Success));
        Assert.That(() => Read(plan, [5, 6], out _), Throws.Exception);
    }

    [Test]
    public void Write_NodeRefValue_WritesReferencedValue()
    {
        var plan = Build(Group("Root", [], Field("A", "sys.uint8"), Valued("Copy", new SchemaNodeRef { Path = "Root.A" })));
        var output = new ArrayBufferWriter<byte>();

        var bytes = WriteBytes(new WriterSession(plan, output), output, new() { ["Root.A"] = (byte)5 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 5, 5 }));
    }
}
