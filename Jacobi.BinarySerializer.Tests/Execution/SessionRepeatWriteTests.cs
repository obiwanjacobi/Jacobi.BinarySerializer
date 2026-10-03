using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>Writing repeats whose count is a constant, a field value or a value the developer published.</summary>
public class SessionRepeatWriteTests
{
    private static ExecutionPlan Plan(SchemaValueOrRef<int> count)
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = count };
        repeat.ChildList.Add(Field("Item", SchemaDataType.Int16));
        return Build(Group("Root", [], Field("Length"), repeat));
    }

    [Test]
    public void Write_RepeatCountFromField_WritesEachItem()
    {
        var plan = Plan(new SchemaValueRef { Reference = "Root.Length" });
        var source = new ItemSource(new() { ["Root.Length"] = 2 }, [1, 2]);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(source);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 2, 0, 0, 0, 1, 0, 2, 0 }));
        Assert.That(source.Entered, Is.EqualTo(new[] { 0, 1 }));
    }

    [Test]
    public void Write_RepeatCountPrefilledByDeveloper_WritesEachItem()
    {
        var plan = Plan(new SchemaValueRef { Reference = "hdr/count" });
        var source = new ItemSource(new() { ["Root.Length"] = 3 }, [7, 8, 9]);
        var output = new ArrayBufferWriter<byte>();
        var session = new WriterSession(plan, output);
        session.Publish("hdr", "count", 3);

        session.Write(source);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 3, 0, 0, 0, 7, 0, 8, 0, 9, 0 }));
    }

    [Test]
    public void Write_RepeatConstantCountZero_WritesNoItems()
    {
        var source = new ItemSource(new() { ["Root.Length"] = 0 }, []);
        var output = new ArrayBufferWriter<byte>();

        new WriterSession(Plan(0), output).Write(source);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
        Assert.That(source.Entered, Is.Empty);
    }

    [Test]
    public void Write_RepeatCountDiffersFromModel_Throws()
    {
        var plan = Plan(new SchemaValueRef { Reference = "Root.Length" });
        var source = new ItemSource(new() { ["Root.Length"] = 3 }, [1, 2]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Items").And.Contain("3").And.Contain("2"));
    }

    [Test]
    public void Write_RepeatReferencesUnpublishedValue_Throws()
    {
        var plan = Plan(new SchemaValueRef { Reference = "hdr/count" });
        var source = new ItemSource(new() { ["Root.Length"] = 1 }, [1]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Items").And.Contain("hdr/count"));
    }

    private sealed class ItemSource(Dictionary<string, object?> values, short[] items, int index = -1, List<int>? entered = null) : IValueSource
    {
        public List<int> Entered { get; } = entered ?? [];

        public bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
        {
            if (index >= 0)
            {
                field = new LogicalField(context.Name, typeof(short), items[index]);
                return true;
            }

            if (values.TryGetValue(context.Path, out var value))
            {
                field = new LogicalField(context.Name, value!.GetType(), value);
                return true;
            }

            field = null;
            return false;
        }

        public IValueSource EnterGroup(GroupContext context) => this;
        public int GetCount(RepeatContext context) => items.Length;

        public IValueSource EnterItem(RepeatContext context, int index)
        {
            Entered.Add(index);
            return new ItemSource(values, items, index, Entered);
        }

        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => throw new NotSupportedException();
    }
}
