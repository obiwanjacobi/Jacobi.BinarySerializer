using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>A choice whose selected index refers to a field value (by schema path).</summary>
public class SessionChoiceTests
{
    private static ExecutionPlan Plan(SchemaValueOrRef<int> selectedIndex)
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = selectedIndex };
        choice.ChildList.Add(Field("A", SchemaDataType.Int16));
        choice.ChildList.Add(Field("B", SchemaDataType.Int16));
        return Build(Group("Root", [], Field("Kind"), choice));
    }

    private static ExecutionPlan PlanByKind()
        => Plan(new SchemaNodeRef { Path = "Root.Kind" });

    [Test]
    public void Write_ChoiceByFieldValue_WritesOnlySelectedAlternative()
    {
        var asked = new List<string>();
        var source = new Source(new() { ["Root.Kind"] = 1, ["Root.Pick.B"] = (short)7 }, asked);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(PlanByKind(), output).Write(source);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 0, 0, 0, 7, 0 }));
        Assert.That(asked, Is.EqualTo(new[] { "Root.Kind", "Root.Pick.B" }));
    }

    [Test]
    public void Write_ChoiceConstantIndex_WritesThatAlternative()
    {
        var source = new Source(new() { ["Root.Kind"] = 0, ["Root.Pick.A"] = (short)5 }, []);
        var output = new ArrayBufferWriter<byte>();

        new WriterSession(Plan(0), output).Write(source);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0, 5, 0 }));
    }

    [Test]
    public void Write_ChoiceIndexOutOfRange_Throws()
    {
        var source = new Source(new() { ["Root.Kind"] = 2 }, []);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WriterSession(PlanByKind(), new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Pick").And.Contain("out of range"));
    }

    [Test]
    public void Write_ChoiceReferencesUnpublishedValue_Throws()
    {
        var source = new Source(new() { ["Root.Kind"] = 1 }, []);
        var plan = Plan(new SchemaPubRef { Namespace = "hdr", Name = "kind" });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Pick").And.Contain("hdr/kind"));
    }

    [Test]
    public void Read_ChoiceByFieldValue_ReadsOnlySelectedAlternative()
    {
        var sink = new Sink();
        var input = new ReadOnlySequence<byte>(new byte[] { 1, 0, 0, 0, 7, 0 });

        var result = new ReaderSession(PlanByKind()).Read(input, sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Has.Count.EqualTo(2));
        Assert.That(sink.Values["Root.Kind"], Is.EqualTo(1));
        Assert.That(sink.Values["Root.Pick.B"], Is.EqualTo((short)7));
    }

    [Test]
    public void Read_ChoiceIndexOutOfRange_Throws()
    {
        var input = new ReadOnlySequence<byte>(new byte[] { 5, 0, 0, 0, 7, 0 });

        Assert.Throws<InvalidOperationException>(() => new ReaderSession(PlanByKind()).Read(input, new Sink()));
    }

    private sealed class Source(Dictionary<string, object?> values, List<string> asked) : IFieldSource
    {
        public bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
        {
            asked.Add(context.Path);
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
