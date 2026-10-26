using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>'ref:^.Sibling' and 'ref:^.^.Group.Node': node references relative to the referring node.</summary>
public class RelativeReferenceTests
{
    [Test]
    public void Parse_Relative_KeepsTheUpSegments()
    {
        Assert.That(SchemaNodeRef.TryParse("ref:^.^.Header.Len", out var nodeRef), Is.True);
        Assert.That(nodeRef!.Path, Is.EqualTo("^.^.Header.Len"));
        Assert.That(nodeRef.IsRelative, Is.True);
        Assert.That(nodeRef.ToString(), Is.EqualTo("ref:^.^.Header.Len"));
    }

    [Test]
    public void Parse_Absolute_IsNotRelative()
    {
        Assert.That(SchemaNodeRef.TryParse("ref:Root.Len", out var nodeRef), Is.True);
        Assert.That(nodeRef!.IsRelative, Is.False);
    }

    [TestCase("ref:^")]
    [TestCase("ref:^.^")]
    [TestCase("ref:Root.^.Len")]
    [TestCase("ref:^[1].Len")]
    [TestCase("ref:^Len")]
    // a relative path keeps the instance of the referrer: no indices
    [TestCase("ref:^.Rows[].Len")]
    [TestCase("ref:^.Rows[1].Len")]
    public void Parse_InvalidUp_Fails(string text)
        => Assert.That(SchemaNodeRef.TryParse(text, out _), Is.False);

    [TestCase("ref:^.Len", "Root.Group.Field", "Root.Group.Len")]
    [TestCase("ref:^.^.Header.Len", "Root.Group.Field", "Root.Header.Len")]
    [TestCase("ref:^.^.Len", "Root.Field", null)]
    [TestCase("ref:Root.Len", "Root.Group.Field", "Root.Len")]
    public void Resolve_AgainstTheReferrer(string text, string referrer, string? expected)
    {
        SchemaNodeRef.TryParse(text, out var nodeRef);

        Assert.That(nodeRef!.Resolve(referrer)?.Path, Is.EqualTo(expected));
    }

    // Root { Rows: repeat(2) { N, Items: repeat(<count>) { Item } }, Tail }
    private static ExecutionPlan RowsPlan(string countRef)
    {
        SchemaNodeRef.TryParse(countRef, out var nodeRef);
        var items = new SchemaRepeat { Name = "Items", Count = nodeRef! };
        items.MemberList.Add(Field("Item", "sys.int8"));
        var rows = new SchemaRepeat { Name = "Rows", Count = 2 };
        rows.MemberList.AddRange([Field("N", "sys.int8"), items]);
        return Build(Group("Root", [], rows, Field("Tail", "sys.int8")));
    }

    [Test]
    public void Build_InARepeat_KeepsTheItemOfTheReferrer()
    {
        var plan = RowsPlan("ref:^.N");

        var items = (RepeatInfo)((RepeatInfo)plan.Root.Members[0]).Members[1];
        Assert.That(items.Count.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Rows.N", new InstancePath([InstancePath.Current]))));
    }

    [Test]
    public void Build_IntoAnotherRepeat_Throws()
    {
        var count = new SchemaRepeat { Name = "Items", Count = 1 };
        count.MemberList.Add(Field("Len", "sys.int8"));
        SchemaNodeRef.TryParse("ref:^.Items.Len", out var nodeRef);
        var data = new SchemaRepeat { Name = "Data", Count = nodeRef! };
        data.MemberList.Add(Field("Item", "sys.int8"));

        var ex = Assert.Throws<ExecutionPlanException>(() => Build(Group("Root", [], count, data)));

        Assert.That(ex!.Errors[0], Does.Contain("reaches into the repeat 'Root.Items'"));
    }

    [Test]
    public void Read_InARepeat_UsesTheValueOfTheSameItem()
    {
        var plan = RowsPlan("ref:^.N");
        // row 0: N=1, one item; row 1: N=2, two items; then Tail
        var bytes = new byte[] { 1, 10, 2, 20, 30, 99 };
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((sbyte)99));
    }

    // Root { Header { Len }, Body { Items: repeat(<count>) { Item:Int16 } } }
    private static ExecutionPlan NestedPlan(string countRef)
    {
        SchemaNodeRef.TryParse(countRef, out var nodeRef);
        var items = new SchemaRepeat { Name = "Items", Count = nodeRef! };
        items.MemberList.Add(Field("Item", "sys.int16"));
        return Build(Group("Root", [], Group("Header", [], Field("Len")), Group("Body", [], items)));
    }

    [Test]
    public void Build_UpTwoAndDown_BindsTheAbsolutePath()
    {
        var plan = NestedPlan("ref:^.^.Header.Len");

        var items = (RepeatInfo)((GroupInfo)plan.Root.Members[1]).Members[0];
        Assert.That(items.Count.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Header.Len", default)));
    }

    [Test]
    public void Build_UpBeyondTheRoot_Throws()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => NestedPlan("ref:^.^.^.Header.Len"));

        Assert.That(ex!.Errors[0], Does.Contain("beyond the root"));
    }

    [Test]
    public void Build_UnknownRelativeTarget_Throws()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => NestedPlan("ref:^.Len"));

        Assert.That(ex!.Errors[0], Does.Contain("Root.Body.Len"));
    }

    [Test]
    public void Read_Sibling_UsesTheSiblingsValue()
    {
        SchemaNodeRef.TryParse("ref:^.Len", out var nodeRef);
        var items = new SchemaRepeat { Name = "Items", Count = nodeRef! };
        items.MemberList.Add(Field("Item", "sys.int16"));
        var plan = Build(Group("Root", [], Field("Len"), items));
        var bytes = new byte[] { 2, 0, 0, 0, 10, 0, 20, 0 };
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));
        Assert.That(sink.Values["Root.Items.Item"], Is.EqualTo((short)20));
    }

    [Test]
    public void Read_UpTwoAndDown_UsesThatValue()
    {
        var plan = NestedPlan("ref:^.^.Header.Len");
        var bytes = new byte[] { 2, 0, 0, 0, 10, 0, 20, 0 };
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(Jacobi.BinarySerializer.Processor.ReadResult.Success));
        Assert.That(sink.Values["Root.Body.Items.Item"], Is.EqualTo((short)20));
    }
}
