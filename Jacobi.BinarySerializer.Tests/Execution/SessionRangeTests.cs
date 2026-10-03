using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>The flat field API and writing/reading a range of fields.</summary>
public class SessionRangeTests
{
    // Root { A, G { B, H { C }, D }, E }  (all Int32)
    private static SchemaGroup Schema(params SchemaProcessorRef[] rootProcessors)
        => Group("Root", rootProcessors,
            Field("A"),
            Group("G", [], Field("B"), Group("H", [], Field("C")), Field("D")),
            Field("E"));

    private static readonly Dictionary<string, object?> AllValues = new()
    {
        ["Root.A"] = 1,
        ["Root.G.B"] = 2,
        ["Root.G.H.C"] = 3,
        ["Root.G.D"] = 4,
        ["Root.E"] = 5,
    };

    // ---- flat API ----

    [Test]
    public void Write_FlatSource_WritesAllFieldsInPlanOrder()
    {
        var asked = new List<string>();

        var bytes = Write(Build(Schema()), new FlatSource(AllValues, asked));

        Assert.That(asked, Is.EqualTo(new[] { "Root.A", "Root.G.B", "Root.G.H.C", "Root.G.D", "Root.E" }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0 }));
    }

    [Test]
    public void Read_FlatSink_ReceivesAllFieldsInPlanOrder()
    {
        var plan = Build(Schema());
        var sink = new FlatSink();

        var result = new ReaderSession(plan).Read(
            new ReadOnlySequence<byte>(Write(plan, new DictSource(AllValues))), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Is.EqualTo(AllValues));
    }

    // ---- ranges ----

    [Test]
    public void Write_RangeOfFields_OnlyWritesThoseFields()
    {
        var plan = Build(Schema());
        var asked = new List<string>();

        var bytes = Write(plan, new FlatSource(AllValues, asked), plan.CreateRange("Root.G.B", "Root.G.D"));

        Assert.That(asked, Is.EqualTo(new[] { "Root.G.B", "Root.G.H.C", "Root.G.D" }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0 }));
    }

    [Test]
    public void Write_RangeAcrossGroupLevels_StartsAndEndsInDifferentGroups()
    {
        var plan = Build(Schema());

        var bytes = Write(plan, new DictSource(AllValues), plan.CreateRange("Root.G.H.C", "Root.E"));

        Assert.That(bytes, Is.EqualTo(new byte[] { 3, 0, 0, 0, 4, 0, 0, 0, 5, 0, 0, 0 }));
    }

    [Test]
    public void Write_RangeOfGroup_CoversAllFieldsOfTheGroup()
    {
        var plan = Build(Schema());

        var bytes = Write(plan, new DictSource(AllValues), plan.CreateRange("Root.G", "Root.G"));

        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 0, 0, 0, 3, 0, 0, 0, 4, 0, 0, 0 }));
    }

    [Test]
    public void Write_RangeOfSingleField_WritesOnlyThatField()
    {
        var plan = Build(Schema());

        var bytes = Write(plan, new DictSource(AllValues), plan.CreateRange("Root.G.H.C", "Root.G.H.C"));

        Assert.That(bytes, Is.EqualTo(new byte[] { 3, 0, 0, 0 }));
    }

    [Test]
    public void Write_Range_EntersOnlyGroupsLeadingToTheRange()
    {
        var plan = Build(Schema());
        var events = new List<string>();

        Write(plan, new DictSource(AllValues, events), plan.CreateRange("Root.G.H.C", "Root.G.H.C"));

        Assert.That(events, Is.EqualTo(new[] { "Enter:Root.G", "Enter:Root.G.H" }));
    }

    [Test]
    public void Write_Range_TriggersLayoutBeginEndForTraversedGroupsOnly()
    {
        var log = new List<string>();
        var root = Group("Root", [Ref("layout")],
            Field("A"),
            Group("G", [Ref("layout")], Field("B")),
            Group("X", [Ref("layout")], Field("C")));
        var plan = Build(root, log);

        Write(plan, new DictSource(new() { ["Root.A"] = 1, ["Root.G.B"] = 2, ["Root.X.C"] = 3 }),
            plan.CreateRange("Root.G.B", "Root.G.B"));

        Assert.That(log, Is.EqualTo(new[] { "Begin:Root", "Begin:Root.G", "Write:Root.G.B", "End:Root.G", "End:Root" }));
    }

    [Test]
    public void Write_Range_StillAppliesRootStreamProcessor()
    {
        var plan = Build(Schema(Ref("stream")));

        var bytes = Write(plan, new DictSource(AllValues), plan.CreateRange("Root.E", "Root.E"));

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xFF, 5, 0, 0, 0 }));
    }

    [Test]
    public void Read_Range_OnlyReadsThoseFields()
    {
        var plan = Build(Schema());
        var range = plan.CreateRange("Root.G.B", "Root.G.D");
        var bytes = Write(plan, new DictSource(AllValues), range);
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink, range);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?>
        {
            ["Root.G.B"] = 2,
            ["Root.G.H.C"] = 3,
            ["Root.G.D"] = 4,
        }));
        Assert.That(sink.Events, Is.EqualTo(new[]
        {
            "Enter:Root.G", "Enter:Root.G.H", "Complete:Root.G.H", "Complete:Root.G", "Complete:Root"
        }));
    }

    [Test]
    public void Read_RangeNeedsOnlyTheRangeBytes()
    {
        var plan = Build(Schema());
        var range = plan.CreateRange("Root.G.H.C", "Root.G.H.C");
        var sink = new FlatSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 3, 0, 0, 0 }), sink, range);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?> { ["Root.G.H.C"] = 3 }));
    }

    [Test]
    public void RoundTrip_WriteAndReadRangesMayDiffer()
    {
        var plan = Build(Schema());
        var bytes = Write(plan, new DictSource(AllValues), plan.CreateRange("Root.G.B", "Root.G.D"));
        var sink = new DictSink();

        // the reader only wants the first field of what was written
        new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink, plan.CreateRange("Root.G.B", "Root.G.B"));

        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?> { ["Root.G.B"] = 2 }));
    }

    [Test]
    public void Write_RangeValueMissing_ThrowsWithPath()
    {
        var plan = Build(Schema());

        var ex = Assert.Throws<InvalidOperationException>(
            () => Write(plan, new DictSource([]), plan.CreateRange("Root.G.B", "Root.G.B")));

        Assert.That(ex!.Message, Does.Contain("Root.G.B"));
    }

    // ---- creating a range ----

    [Test]
    public void CreateRange_UnknownPath_Throws()
    {
        var plan = Build(Schema());

        Assert.Throws<ArgumentException>(() => plan.CreateRange("Root.Nope", "Root.E"));
        Assert.Throws<ArgumentException>(() => plan.CreateRange("Root.A", "Root.Nope"));
    }

    [Test]
    public void CreateRange_EndBeforeStart_Throws()
    {
        var plan = Build(Schema());

        Assert.Throws<ArgumentException>(() => plan.CreateRange("Root.E", "Root.A"));
    }

    [Test]
    public void CreateRange_GroupBounds_ResolveToFirstAndLastField()
    {
        var plan = Build(Schema());

        var range = plan.CreateRange("Root.G", "Root.G");

        Assert.That(range.First.Path, Is.EqualTo("Root.G.B"));
        Assert.That(range.Last.Path, Is.EqualTo("Root.G.D"));
    }

    [Test]
    public void Find_ReturnsNodeByPath()
    {
        var plan = Build(Schema());

        Assert.That(plan.Find("Root.G.H.C"), Is.InstanceOf<FieldInfo>());
        Assert.That(plan.Find("Root.G.H"), Is.InstanceOf<GroupInfo>());
        Assert.That(plan.Find("Root.Nope"), Is.Null);
    }

    // ---- helpers ----

    private static byte[] Write(ExecutionPlan plan, IValueSource source, PlanRange? range = null)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(source, range), Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Write(ExecutionPlan plan, IFieldSource source, PlanRange? range = null)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(source, range), Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    /// <summary>A model that only implements the flat interface: no groups, just fields.</summary>
    private sealed class FlatSource(Dictionary<string, object?> values, List<string> asked) : IFieldSource
    {
        public bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
        {
            asked.Add(context.Path);
            if (values.TryGetValue(context.Path, out var value))
            {
                field = new LogicalField(context.Name, typeof(int), value);
                return true;
            }
            field = null;
            return false;
        }
    }

    private sealed class FlatSink : IFieldSink
    {
        public Dictionary<string, object?> Values { get; } = [];
        public void SetField(FieldContext context, LogicalField value) => Values[context.Path] = value.Value;
    }
}
