using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>Equality, overlap and containment of <see cref="PlanRange"/>s.</summary>
public class PlanRangeComparisonTests
{
    // Root { A, G { B, H { C }, D }, E }
    private static ExecutionPlan Plan()
        => Build(Group("Root", [],
            Field("A"),
            Group("G", [], Field("B"), Group("H", [], Field("C")), Field("D")),
            Field("E")));

    // Root { Items[4] { X, Y } }
    private static ExecutionPlan RepeatPlan()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = 4 };
        repeat.MemberList.Add(Field("X", "sys.int16"));
        repeat.MemberList.Add(Field("Y", "sys.int16"));
        return Build(Group("Root", [], repeat));
    }

    private static InstancePath At(int index) => InstancePath.Empty.Append(index);

    // ---- equality ----

    [Test]
    public void Equals_SameBounds_AreEqual()
    {
        var plan = Plan();
        var a = plan.CreateRange("Root.G.B", "Root.E");
        var b = plan.CreateRange("Root.G.B", "Root.E");

        Assert.That(a.Equals(b), Is.True);
        Assert.That(a == b, Is.True);
        Assert.That(a != b, Is.False);
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void Equals_GroupBoundsEqualToItsFieldBounds()
    {
        var plan = Plan();
        var group = plan.CreateRange("Root.G");
        var fields = plan.CreateRange("Root.G.B", "Root.G.D");

        Assert.That(group, Is.EqualTo(fields));
        Assert.That(group.GetHashCode(), Is.EqualTo(fields.GetHashCode()));
    }

    [Test]
    public void Equals_DifferentBounds_AreNotEqual()
    {
        var plan = Plan();

        Assert.That(plan.CreateRange("Root.A", "Root.E") == plan.CreateRange("Root.A", "Root.G.D"), Is.False);
        Assert.That(plan.CreateRange("Root.A", "Root.E").Equals(null), Is.False);
    }

    [Test]
    public void Equals_DifferentPlans_AreNotEqual()
    {
        Assert.That(Plan().CreateRange("Root.A", "Root.E"), Is.Not.EqualTo(Plan().CreateRange("Root.A", "Root.E")));
    }

    [Test]
    public void Equals_Instances_ComparedPerItem()
    {
        var plan = RepeatPlan();

        Assert.That(plan.CreateRange("Root.Items.X", At(1), "Root.Items.Y", At(2)),
            Is.EqualTo(plan.CreateRange("Root.Items.X", At(1), "Root.Items.Y", At(2))));
        Assert.That(plan.CreateRange("Root.Items.X", At(1), "Root.Items.Y", At(2)),
            Is.Not.EqualTo(plan.CreateRange("Root.Items.X", At(1), "Root.Items.Y", At(3))));
        // an explicit first item equals the implicit first item
        Assert.That(plan.CreateRange("Root.Items.X", At(0), "Root.Items.Y", InstancePath.Empty),
            Is.EqualTo(plan.CreateRange("Root.Items.X", "Root.Items.Y")));
    }

    [Test]
    public void Operators_HandleNull()
    {
        PlanRange? none = null;
        var range = Plan().CreateRange("Root.A", "Root.E");

        Assert.That(none == null, Is.True);
        Assert.That(range == none, Is.False);
        Assert.That(none != range, Is.True);
    }

    // ---- overlap ----

    [Test]
    public void Overlaps_SharedField_IsTrue()
    {
        var plan = Plan();
        var a = plan.CreateRange("Root.A", "Root.G.H.C");
        var b = plan.CreateRange("Root.G.H.C", "Root.E");

        Assert.That(a.Overlaps(b), Is.True);
        Assert.That(b.Overlaps(a), Is.True);
    }

    [Test]
    public void Overlaps_AdjacentRanges_IsFalse()
    {
        var plan = Plan();
        var a = plan.CreateRange("Root.A", "Root.G.B");
        var b = plan.CreateRange("Root.G.H.C", "Root.E");

        Assert.That(a.Overlaps(b), Is.False);
        Assert.That(b.Overlaps(a), Is.False);
    }

    [Test]
    public void Overlaps_ContainedRange_IsTrue()
    {
        var plan = Plan();

        Assert.That(plan.CreateRange("Root.A", "Root.E").Overlaps(plan.CreateRange("Root.G.H")), Is.True);
    }

    [Test]
    public void Overlaps_DifferentItems_IsFalse()
    {
        var plan = RepeatPlan();
        var a = plan.CreateRange("Root.Items.X", At(0), "Root.Items.Y", At(1));
        var b = plan.CreateRange("Root.Items.X", At(2), "Root.Items.Y", At(3));

        Assert.That(a.Overlaps(b), Is.False);
    }

    [Test]
    public void Overlaps_LaterFieldOfEarlierItem_IsTrue()
    {
        var plan = RepeatPlan();
        var a = plan.CreateRange("Root.Items.X", At(0), "Root.Items.X", At(1));
        var b = plan.CreateRange("Root.Items.Y", At(0), "Root.Items.Y", At(0));

        Assert.That(a.Overlaps(b), Is.True);
    }

    [Test]
    public void Overlaps_DifferentPlans_IsFalse()
    {
        Assert.That(Plan().CreateRange("Root.A", "Root.E").Overlaps(Plan().CreateRange("Root.A", "Root.E")), Is.False);
    }

    // ---- containment ----

    [Test]
    public void Contains_InnerRange_IsTrue()
    {
        var plan = Plan();
        var outer = plan.CreateRange("Root.A", "Root.E");
        var inner = plan.CreateRange("Root.G");

        Assert.That(outer.Contains(inner), Is.True);
        Assert.That(inner.Contains(outer), Is.False);
    }

    [Test]
    public void Contains_Itself_IsTrue()
    {
        var range = Plan().CreateRange("Root.G");

        Assert.That(range.Contains(range), Is.True);
    }

    [Test]
    public void Contains_PartialOverlap_IsFalse()
    {
        var plan = Plan();

        Assert.That(plan.CreateRange("Root.A", "Root.G.H.C").Contains(plan.CreateRange("Root.G.B", "Root.E")), Is.False);
    }

    [Test]
    public void Contains_AllItemsContainsSomeItems()
    {
        var plan = RepeatPlan();
        var all = plan.CreateRange("Root.Items.X", "Root.Items.Y");
        var some = plan.CreateRange("Root.Items.X", At(1), "Root.Items.Y", At(2));

        Assert.That(all.Contains(some), Is.True);
        Assert.That(some.Contains(all), Is.False);
    }
}
