using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class PlanCursorTests
{
    [Test]
    public void Walk_VisitsNodesInDocumentOrder()
    {
        var root = Group("Root", Field("A"), Group("G", Field("B")), Field("C"));

        var steps = Walk(Build(root));

        Assert.That(steps, Is.EqualTo(new[]
        {
            "EnterGroup:Root@",
            "Field:Root.A@Root",
            "EnterGroup:Root.G@Root",
            "Field:Root.G.B@Root.G",
            "ExitGroup:Root.G@Root.G",
            "Field:Root.C@Root",
            "ExitGroup:Root@Root",
            "Done:@",
        }));
    }

    [Test]
    public void Walk_EmptyRoot_EntersAndExits()
    {
        var steps = Walk(Build(Group("Root")));

        Assert.That(steps, Is.EqualTo(new[] { "EnterGroup:Root@", "ExitGroup:Root@Root", "Done:@" }));
    }

    [Test]
    public void Depth_TracksEnteredGroups()
    {
        var cursor = new PlanCursor<string>(Build(Group("Root", Group("G", Field("A")))));
        var depths = new List<int>();

        CursorStep<string> step;
        do
        {
            step = cursor.Next();
            if (step.Kind == CursorStepKind.EnterGroup)
            {
                cursor.Enter(step.Node!.Path);
            }
            depths.Add(cursor.Depth);
        } while (step.Kind != CursorStepKind.Done);

        // Enter Root, Enter G, Field A, Exit G, Exit Root, Done
        Assert.That(depths, Is.EqualTo(new[] { 1, 2, 2, 1, 0, 0 }));
    }

    [Test]
    public void CurrentGroup_IsInnermostEnteredGroup()
    {
        var cursor = new PlanCursor<string>(Build(Group("Root", Group("G", Field("A")))));

        Assert.That(cursor.CurrentGroup, Is.Null);

        cursor.Next();
        cursor.Enter("Root");
        Assert.That(cursor.CurrentGroup!.Name, Is.EqualTo("Root"));

        cursor.Next();
        cursor.Enter("Root.G");
        Assert.That(cursor.CurrentGroup!.Name, Is.EqualTo("G"));
    }

    [Test]
    public void Next_AfterDone_StaysDone()
    {
        var cursor = new PlanCursor<string>(Build(Group("Root")));
        Drain(cursor);

        Assert.That(cursor.Next().Kind, Is.EqualTo(CursorStepKind.Done));
        Assert.That(cursor.Next().Kind, Is.EqualTo(CursorStepKind.Done));
    }

    [Test]
    public void Next_WithoutEnterAfterEnterGroupStep_Throws()
    {
        var cursor = new PlanCursor<string>(Build(Group("Root")));
        cursor.Next();   // EnterGroup root

        var ex = Assert.Throws<InvalidOperationException>(() => cursor.Next());

        Assert.That(ex!.Message, Does.Contain("Root"));
    }

    [Test]
    public void Enter_WithoutEnterGroupStep_Throws()
    {
        var cursor = new PlanCursor<string>(Build(Group("Root")));

        Assert.Throws<InvalidOperationException>(() => cursor.Enter("x"));
    }

    [Test]
    public void Enter_CalledTwice_Throws()
    {
        var cursor = new PlanCursor<string>(Build(Group("Root")));
        cursor.Next();
        cursor.Enter("Root");

        Assert.Throws<InvalidOperationException>(() => cursor.Enter("again"));
    }

    [Test]
    public void Next_Repeat_IsNotSupportedYet()
    {
        var repeat = new SchemaRepeat { Name = "R", Count = 2 };
        var cursor = new PlanCursor<string>(Build(Group("Root", repeat)));
        cursor.Next();
        cursor.Enter("Root");

        Assert.Throws<NotSupportedException>(() => cursor.Next());
    }

    [Test]
    public void Next_Choice_IsNotSupportedYet()
    {
        var choice = new SchemaChoice { Name = "C", SelectedIndex = 0 };
        choice.ChildList.Add(Field("A"));
        var cursor = new PlanCursor<string>(Build(Group("Root", choice)));
        cursor.Next();
        cursor.Enter("Root");

        Assert.Throws<NotSupportedException>(() => cursor.Next());
    }

    [Test]
    public void Cursor_CanBeSuspendedBetweenSteps()
    {
        // all state lives in the cursor, so stepping can stop and continue at any point
        var cursor = new PlanCursor<string>(Build(Group("Root", Field("A"), Field("B"))));
        cursor.Next();
        cursor.Enter("Root");

        var first = cursor.Next();
        // ... suspended here (e.g. waiting for more data) ...
        var second = cursor.Next();

        Assert.That(first.Node!.Name, Is.EqualTo("A"));
        Assert.That(second.Node!.Name, Is.EqualTo("B"));
    }

    // ---- helpers ----

    /// <summary>Drives the cursor like a session would: the scope of a group is its path.</summary>
    private static List<string> Walk(GroupInfo root)
    {
        var cursor = new PlanCursor<string>(root);
        return Drain(cursor);
    }

    private static List<string> Drain(PlanCursor<string> cursor)
    {
        var steps = new List<string>();
        CursorStep<string> step;
        do
        {
            step = cursor.Next();
            steps.Add($"{step.Kind}:{step.Node?.Path}@{step.Scope}");
            if (step.Kind == CursorStepKind.EnterGroup)
            {
                cursor.Enter(step.Node!.Path);
            }
        } while (step.Kind != CursorStepKind.Done);
        return steps;
    }

    private static GroupInfo Build(SchemaGroup root)
        => new ExecutionPlanBuilder(new ProcessorManager()).Build(root).Root;

    private static SchemaField Field(string name)
        => new() { Name = name, Type = SchemaDataType.Int32 };

    private static SchemaGroup Group(string name, params SchemaNode[] children)
    {
        var group = new SchemaGroup { Name = name };
        group.ChildList.AddRange(children);
        return group;
    }
}
