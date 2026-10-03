using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>Verifies the plan tree mirrors the structure of the schema.</summary>
public class ExecutionPlanStructureTests
{
    [Test]
    public void Build_PreservesChildOrder()
    {
        var root = Group("Root", Field("C"), Field("A"), Field("B"));

        var plan = Build(root);

        Assert.That(plan.Root.Children.Select(c => c.Name), Is.EqualTo(new[] { "C", "A", "B" }));
    }

    [Test]
    public void Build_MapsEachNodeKindToMatchingInfoType()
    {
        var root = Group("Root",
            Field("F"),
            Group("G"),
            Repeat("R", 2),
            Choice("C", 0, Field("X")));

        var plan = Build(root);

        var children = plan.Root.Children;
        Assert.That(children[0], Is.TypeOf<FieldInfo>());
        Assert.That(children[1], Is.TypeOf<GroupInfo>());
        Assert.That(children[2], Is.TypeOf<RepeatInfo>());
        Assert.That(children[3], Is.TypeOf<ChoiceInfo>());
    }

    [Test]
    public void Build_InfoNodesReferenceTheirSchemaNodes()
    {
        var field = Field("F");
        var inner = Group("Inner");
        var root = Group("Root", field, inner);

        var plan = Build(root);

        Assert.That(plan.Root.Group, Is.SameAs(root));
        Assert.That(((FieldInfo)plan.Root.Children[0]).Field, Is.SameAs(field));
        Assert.That(((GroupInfo)plan.Root.Children[1]).Group, Is.SameAs(inner));
    }

    [Test]
    public void Build_EmptyGroup_HasNoChildren()
    {
        var plan = Build(Group("Root"));

        Assert.That(plan.Root.Children, Is.Empty);
    }

    [Test]
    public void Build_DeeplyNestedGroups_HaveFullPathsAndParentChain()
    {
        var leaf = Field("Leaf");
        var root = Group("L0", Group("L1", Group("L2", leaf)));

        var plan = Build(root);

        var l1 = (GroupInfo)plan.Root.Children.Single();
        var l2 = (GroupInfo)l1.Children.Single();
        var leafInfo = l2.Children.Single();

        Assert.That(leafInfo.Path, Is.EqualTo("L0.L1.L2.Leaf"));
        Assert.That(leafInfo.Parent, Is.SameAs(l2));
        Assert.That(l2.Parent, Is.SameAs(l1));
        Assert.That(l1.Parent, Is.SameAs(plan.Root));
        Assert.That(plan.Root.Parent, Is.Null);
    }

    [Test]
    public void Build_SiblingIndexes_AreSequentialPerGroup()
    {
        var root = Group("Root",
            Group("G1", Field("A"), Field("B")),
            Group("G2", Field("C"), Field("D"), Field("E")));

        var plan = Build(root);

        Assert.That(plan.Root.Children.Select(c => c.Index), Is.EqualTo(new[] { 0, 1 }));
        var g1 = (GroupInfo)plan.Root.Children[0];
        var g2 = (GroupInfo)plan.Root.Children[1];
        Assert.That(g1.Children.Select(c => c.Index), Is.EqualTo(new[] { 0, 1 }));
        Assert.That(g2.Children.Select(c => c.Index), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Build_SameNameInDifferentGroups_GetsDistinctPaths()
    {
        var root = Group("Root",
            Group("G1", Field("Value")),
            Group("G2", Field("Value")));

        var plan = Build(root);

        var paths = plan.Root.Children
            .Cast<GroupInfo>()
            .SelectMany(g => g.Children)
            .Select(c => c.Path);
        Assert.That(paths, Is.EqualTo(new[] { "Root.G1.Value", "Root.G2.Value" }));
    }

    [Test]
    public void Build_RepeatOfGroup_KeepsNestedChildren()
    {
        var root = Group("Root",
            Repeat("Items", 4, Group("Item", Field("Id"), Field("Name"))));

        var plan = Build(root);

        var repeat = (RepeatInfo)plan.Root.Children.Single();
        var item = (GroupInfo)repeat.Children.Single();
        Assert.That(repeat.Count.Value, Is.EqualTo(4));
        Assert.That(item.Path, Is.EqualTo("Root.Items.Item"));
        Assert.That(item.Children.Select(c => c.Name), Is.EqualTo(new[] { "Id", "Name" }));
    }

    [Test]
    public void Build_ChoiceWithGroupAlternatives_KeepsAllAlternatives()
    {
        var root = Group("Root",
            Choice("Pick", 1,
                Group("OptA", Field("A1")),
                Group("OptB", Field("B1"), Field("B2"))));

        var plan = Build(root);

        var choice = (ChoiceInfo)plan.Root.Children.Single();
        Assert.That(choice.SelectedIndex.Value, Is.EqualTo(1));
        Assert.That(choice.Children.Select(c => c.Name), Is.EqualTo(new[] { "OptA", "OptB" }));
        Assert.That(((GroupInfo)choice.Children[1]).Children, Has.Count.EqualTo(2));
    }

    [Test]
    public void Build_NestedRepeatAndChoice_BuildsFullTree()
    {
        var root = Group("Root",
            Field("Header"),
            Repeat("Records", 2,
                Group("Record",
                    Field("Kind"),
                    Choice("Body", 0, Field("Text"), Field("Number")))));

        var plan = Build(root);

        var records = (RepeatInfo)plan.Root.Children[1];
        var record = (GroupInfo)records.Children.Single();
        var body = (ChoiceInfo)record.Children[1];

        Assert.That(plan.Root.Children[0], Is.TypeOf<FieldInfo>());
        Assert.That(body.Path, Is.EqualTo("Root.Records.Record.Body"));
        Assert.That(body.Children.Select(c => c.Path),
            Is.EqualTo(new[] { "Root.Records.Record.Body.Text", "Root.Records.Record.Body.Number" }));
        Assert.That(body.Parent, Is.SameAs(record));
        Assert.That(record.Parent, Is.SameAs(records));
    }

    [Test]
    public void Build_AllNodesInTree_HaveAPipeline()
    {
        var root = Group("Root",
            Field("A"),
            Group("G", Field("B")),
            Repeat("R", 1, Field("C")));

        var plan = Build(root);

        Assert.That(Flatten(plan.Root).All(n => n.Pipeline is not null), Is.True);
    }

    [Test]
    public void Build_NodeCount_MatchesSchema()
    {
        var root = Group("Root",
            Field("A"),
            Group("G", Field("B"), Field("C")),
            Repeat("R", 1, Field("D")),
            Choice("Ch", 0, Field("E"), Field("F")));

        var plan = Build(root);

        // Root + A + G + B + C + R + D + Ch + E + F
        Assert.That(Flatten(plan.Root).Count(), Is.EqualTo(10));
    }

    [Test]
    public void Build_EveryChild_PointsBackToItsParentAtItsIndex()
    {
        var root = Group("Root",
            Field("A"),
            Group("G", Field("B"), Repeat("R", 1, Field("C"))));

        var plan = Build(root);

        foreach (var node in Flatten(plan.Root).Where(n => n.Parent is not null))
        {
            Assert.That(node.Parent!.Children[node.Index], Is.SameAs(node), node.Path.Value);
        }
    }

    // ---- helpers ----

    private static IEnumerable<NodeInfo> Flatten(NodeInfo node)
    {
        yield return node;
        if (node is GroupInfo group)
        {
            foreach (var child in group.Children)
            {
                foreach (var descendant in Flatten(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static ExecutionPlan Build(SchemaGroup root)
        => new ExecutionPlanBuilder(new Processor.ProcessorManager()).Build(root);

    private static SchemaField Field(string name)
        => new() { Name = name, Type = SchemaDataType.Int32 };

    private static SchemaGroup Group(string name, params SchemaNode[] children)
    {
        var group = new SchemaGroup { Name = name };
        group.ChildList.AddRange(children);
        return group;
    }

    private static SchemaRepeat Repeat(string name, int count, params SchemaNode[] children)
    {
        var repeat = new SchemaRepeat { Name = name, Count = count };
        repeat.ChildList.AddRange(children);
        return repeat;
    }

    private static SchemaChoice Choice(string name, int index, params SchemaNode[] children)
    {
        var choice = new SchemaChoice { Name = name, SelectedIndex = index };
        choice.ChildList.AddRange(children);
        return choice;
    }
}
