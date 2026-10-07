using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class SessionRangeInstanceTests
{
    private static ExecutionPlan Plan()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = 4 };
        repeat.MemberList.Add(Field("Item", SchemaDataType.Int16));
        return Build(Group("Root", [], repeat));
    }

    [Test]
    public void Write_RangeWithInstances_VisitsOnlyThoseItems()
    {
        var plan = Plan();
        var range = plan.CreateRange("Root.Items.Item", InstancePath.Empty.Append(1), "Root.Items.Item", InstancePath.Empty.Append(2));
        var source = new RecordingSource();

        new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source, range);

        Assert.That(source.Fields, Is.EqualTo(new[] { "Root.Items.Item[1]", "Root.Items.Item[2]" }));
    }

    [Test]
    public void Write_RangeWithoutInstances_VisitsAllItems()
    {
        var plan = Plan();
        var range = plan.CreateRange("Root.Items.Item", "Root.Items.Item");
        var source = new RecordingSource();

        new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source, range);

        Assert.That(source.Fields, Has.Count.EqualTo(4));
    }

    [Test]
    public void CreateRange_StartAfterEnd_Throws()
    {
        var plan = Plan();

        Assert.Throws<ArgumentException>(() => plan.CreateRange(
            "Root.Items.Item", InstancePath.Empty.Append(3), "Root.Items.Item", InstancePath.Empty.Append(1)));
    }

    [Test]
    public void CreateRange_TooManyIndices_Throws()
    {
        var plan = Plan();

        Assert.Throws<ArgumentException>(() => plan.CreateRange(
            "Root.Items.Item", InstancePath.Empty.Append(0).Append(0), "Root.Items.Item", InstancePath.Empty));
    }

    private sealed class RecordingSource : IValueSource
    {
        public List<string> Fields { get; } = [];

        public SourceResult GetField(FieldContext context) => TryGetField(context, out var found) ? SourceResult.Provided(found) : SourceResult.NoValue();

        private bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
        {
            Fields.Add(context.Path + context.Instance);
            field = new LogicalField(context.Name, typeof(short), (short)1);
            return true;
        }

        public IValueSource EnterGroup(GroupContext context) => this;
        public int GetCount(RepeatContext context) => 4;
        public IValueSource EnterItem(RepeatContext context, int index) => this;
        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => throw new NotSupportedException();
    }
}
