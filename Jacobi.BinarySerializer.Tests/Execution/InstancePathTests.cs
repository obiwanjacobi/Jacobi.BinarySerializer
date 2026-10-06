using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class InstancePathTests
{
    [Test]
    public void Empty_HasNoIndices()
    {
        Assert.That(InstancePath.Empty.IsEmpty, Is.True);
        Assert.That(InstancePath.Empty.Last, Is.EqualTo(-1));
        Assert.That(InstancePath.Empty.ToString(), Is.Empty);
    }

    [Test]
    public void Append_AddsInnermostIndex_AndLeavesOriginalUnchanged()
    {
        var outer = InstancePath.Empty.Append(2);
        var inner = outer.Append(0);
        Assert.That(outer.Length, Is.EqualTo(1));
        Assert.That(inner[0], Is.EqualTo(2));
        Assert.That(inner.Last, Is.Zero);
        Assert.That(inner.ToString(), Is.EqualTo("[2][0]"));
    }

    [Test]
    public void Equality_ComparesIndices()
    {
        Assert.That(InstancePath.Empty.Append(1), Is.EqualTo(new InstancePath([1])));
        Assert.That(InstancePath.Empty.Append(1), Is.Not.EqualTo(InstancePath.Empty.Append(2)));
        Assert.That(InstancePath.Empty, Is.EqualTo(new InstancePath([])));
    }

    [Test]
    public void Append_Negative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InstancePath.Empty.Append(-1));
    }

    [Test]
    public void Write_FieldInRepeat_ReceivesInstanceIndex()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = 3 };
        repeat.ChildList.Add(Field("Item", SchemaDataType.Int16));
        var plan = Build(Group("Root", [], repeat));
        var source = new RecordingSource();
        new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source);
        Assert.That(source.Fields, Is.EqualTo(new[] { "Root.Items.Item[0]", "Root.Items.Item[1]", "Root.Items.Item[2]" }));
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
        public int GetCount(RepeatContext context) => 3;
        public IValueSource EnterItem(RepeatContext context, int index) => this;
        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => throw new NotSupportedException();
    }
}
