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
        repeat.MemberList.Add(Field("Item", "sys.int16"));
        return Build(Group("Root", [], Field("Length"), repeat));
    }

    [Test]
    public void Write_RepeatCountFromField_WritesEachItem()
    {
        var plan = Plan(new SchemaNodeRef { Path = "Root.Length" });
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
        var plan = Plan(new SchemaPubRef { Namespace = "hdr", Name = "count" });
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
    public void Write_RepeatCount_IsGivenToTheModel()
    {
        var plan = Plan(new SchemaNodeRef { Path = "Root.Length" });
        var source = new ItemSource(new() { ["Root.Length"] = 2 }, [1, 2]);

        new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source);

        Assert.That(source.Repeats, Is.EqualTo(new[] { "enter Root.Items 2", "exit Root.Items 2" }));
    }

    [Test]
    public void Write_RepeatCountFieldMissingInModel_DerivesFromTheCountOfTheModel()
    {
        var plan = Plan(new SchemaNodeRef { Path = "Root.Length" });
        var source = new ItemSource([], [1, 2]);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(source);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 2, 0, 0, 0, 1, 0, 2, 0 }));
        Assert.That(source.Repeats, Is.EqualTo(new[] { "open Root.Items", "exit Root.Items 2" }));
    }

    [Test]
    public void Write_RepeatCountFieldMissingInModel_DerivesFromTheItemsWrittenItemByItem()
    {
        var plan = Plan(new SchemaNodeRef { Path = "Root.Length" });
        var source = new ItemSource([], [1, 2, 3]) { ItemByItem = true };
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(source);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 3, 0, 0, 0, 1, 0, 2, 0, 3, 0 }));
        Assert.That(source.Entered, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Write_RepeatCountFieldMissingInModelWithoutOpenRepeatSource_Throws()
    {
        var plan = Plan(new SchemaNodeRef { Path = "Root.Length" });

        var ex = Assert.Throws<SerializationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(new DictSource([])));

        Assert.That(ex!.Message, Does.Contain("Root.Items").And.Contain(nameof(IOpenRepeatSource)));
    }

    [Test]
    public void Write_DerivedCountInsideADerivedSize_BothAreFilledIn()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = new SchemaNodeRef { Path = "Root.Body.Length" } };
        repeat.MemberList.Add(Field("Item", "sys.int16"));
        var body = new SchemaGroup { Name = "Body", ByteSize = new SchemaNodeRef { Path = "Root.Size" }, MemberList = { Field("Length", "sys.uint8"), repeat } };
        var plan = Build(Group("Root", [], Field("Size", "sys.uint8"), body));
        var source = new ItemSource([], [1, 2]);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(source);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 2, 1, 0, 2, 0 }));
    }

    [Test]
    public void Write_DerivedCountAndSizeThatOverlap_Throws()
    {
        // Size measures Body, Length counts Items after Body: Body ends while Length is the innermost deferred field
        var repeat = new SchemaRepeat { Name = "Items", Count = new SchemaNodeRef { Path = "Root.Length" } };
        repeat.MemberList.Add(Field("Item", "sys.int16"));
        var body = new SchemaGroup { Name = "Body", ByteSize = new SchemaNodeRef { Path = "Root.Size" }, MemberList = { Field("X", "sys.uint8") } };
        var plan = Build(Group("Root", [], Field("Size", "sys.uint8"), Field("Length", "sys.uint8"), body, repeat));
        var source = new ItemSource(new() { ["Root.Body.X"] = (byte)7 }, [1, 2]);

        var ex = Assert.Throws<SerializationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Size").And.Contain("Root.Length").And.Contain("nest"));
    }

    [Test]
    public void Write_RepeatReferencesUnpublishedValue_Throws()
    {
        var plan = Plan(new SchemaPubRef { Namespace = "hdr", Name = "count" });
        var source = new ItemSource(new() { ["Root.Length"] = 1 }, [1]);

        var ex = Assert.Throws<SerializationException>(
            () => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source));

        Assert.That(ex!.Message, Does.Contain("Root.Items").And.Contain("hdr/count"));
    }

    // the root scope holds the repeat (with a count, or open: the number of items, or item by item); the items are scopes of their own
    private sealed class ItemSource(Dictionary<string, object?> values, short[] items, int index = -1, List<int>? entered = null) : IOpenRepeatSource
    {
        public List<int> Entered { get; } = entered ?? [];
        public List<string> Repeats { get; } = [];
        public bool ItemByItem { get; init; }

        public OpenRepeatSourceResult EnterOpenRepeat(RepeatContext context)
        {
            Repeats.Add($"open {context.Path}");
            return ItemByItem ? OpenRepeatSourceResult.ItemByItem(this) : OpenRepeatSourceResult.Counted(this, items.Length);
        }

        public OpenRepeatAction NextItem(RepeatContext context, int index) => index < items.Length ? OpenRepeatAction.Next : OpenRepeatAction.End;

        public SourceResult GetField(FieldContext context) => TryGetField(context, out var found) ? SourceResult.Provided(found) : SourceResult.NoValue();

        private bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
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
        public void ExitGroup(GroupContext context) { }
        public IValueSource EnterRepeat(RepeatContext context, int count)
        {
            Repeats.Add($"enter {context.Path} {count}");
            return this;
        }
        public void ExitRepeat(RepeatContext context, int count) => Repeats.Add($"exit {context.Path} {count}");
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }

        public IValueSource EnterItem(RepeatContext context, int index, int count)
        {
            Entered.Add(index);
            return new ItemSource(values, items, index, Entered);
        }

        public IValueSource EnterChoice(ChoiceContext context, int selectedIndex) => throw new NotSupportedException();
    }
}
