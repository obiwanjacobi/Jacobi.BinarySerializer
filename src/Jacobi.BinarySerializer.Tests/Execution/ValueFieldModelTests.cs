using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class ValueFieldModelTests
{
    private static ExecutionPlan CreatePlan()
    {
        var root = SessionTestHelpers.Group("Root", [],
            new SchemaField { Name = "Fixed", DataType = "Int32", Value = "42" },
            SessionTestHelpers.Field("Free", "Int32"));
        return SessionTestHelpers.Build(root);
    }

    [Test]
    public void Write_ValueField_DefaultDoesNotAskTheSource()
    {
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(CreatePlan(), output).Write(new DictSource(new() { ["Root.Free"] = 1 }));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 42, 0, 0, 0, 1, 0, 0, 0 }).Or.EqualTo(new byte[] { 0, 0, 0, 42, 0, 0, 0, 1 }));
    }

    [Test]
    public void Write_ValueField_OptionOn_SourceValueIsUsedAndChecked()
    {
        var output = new ArrayBufferWriter<byte>();

        var mismatch = Assert.Catch(() => new WriterSession(CreatePlan(), output, valueFieldsUseModel: true)
            .Write(new DictSource(new() { ["Root.Fixed"] = 7, ["Root.Free"] = 1 })));

        Assert.That(mismatch, Is.Not.Null);
    }

    [Test]
    public void Read_ValueField_DefaultDoesNotReportToTheSink()
    {
        var plan = CreatePlan();
        var output = new ArrayBufferWriter<byte>();
        new WriterSession(plan, output).Write(new DictSource(new() { ["Root.Free"] = 1 }));
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values.Keys, Is.EquivalentTo(new[] { "Root.Free" }));
    }

    [Test]
    public void Read_ValueField_OptionOn_ReportsToTheSink()
    {
        var plan = CreatePlan();
        var output = new ArrayBufferWriter<byte>();
        new WriterSession(plan, output).Write(new DictSource(new() { ["Root.Free"] = 1 }));
        var sink = new DictSink();

        var result = new ReaderSession(plan, valueFieldsUseModel: true).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values.Keys, Is.EquivalentTo(new[] { "Root.Fixed", "Root.Free" }));
    }
}
