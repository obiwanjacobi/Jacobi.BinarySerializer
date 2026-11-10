using System.Buffers;
using System.Text;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

/// <summary>Processor property values that are references ('ref:'/'pub:'): bound at plan build, resolved per instance at runtime.</summary>
public class ProcessorPropertyReferenceTests
{
    private static SchemaField Text(string name, params (string Name, string Value)[] properties)
        => Field(name, "sys.string", [Ref("string", properties)]);

    private static SchemaRepeat Repeat(string name, int count, params SchemaNode[] members)
    {
        var repeat = new SchemaRepeat { Name = name, Count = count };
        repeat.MemberList.AddRange(members);
        return repeat;
    }

    private static ListSink Read(ExecutionPlan plan, byte[] bytes)
    {
        var sink = new ListSink();
        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink), Is.EqualTo(ReadResult.Success));
        return sink;
    }

    private static byte[] Bytes(params object[] parts)
        => [.. parts.SelectMany(p => p is string text ? Encoding.UTF8.GetBytes(text) : [Convert.ToByte(p)])];

    [Test]
    public void Build_NodeReference_IsBoundOnTheBinding()
    {
        var plan = Build(Group("Root", [], Field("Len", "sys.uint8"), Text("Name", ("byteLength", "ref:^.Len"))));

        var name = (FieldInfo)plan.Root.Members[1];
        var binding = name.Pipeline.FieldProcessors[0];
        Assert.That(binding.References["sys.string.byteLength"], Is.EqualTo(PublishedValueKey.ForPath("Root.Len")));
        Assert.That(((FieldInfo)plan.Root.Members[0]).PublishesValue, Is.True);
    }

    [Test]
    public void ByteLength_RelativeReference_WritesAndReads()
    {
        var root = Group("Root", [], Field("Len", "sys.uint8"), Text("Name", ("byteLength", "ref:^.Len")), Field("Tail", "sys.uint8"));

        var bytes = Write(root, new() { ["Root.Len"] = (byte)5, ["Root.Name"] = "hello", ["Root.Tail"] = (byte)9 }, out var result);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(bytes, Is.EqualTo(Bytes(5, "hello", 9)));
        var sink = Read(Build(root), Bytes(3, "abc", 7));
        Assert.That(sink.Last("Root.Name"), Is.EqualTo("abc"));
        Assert.That(sink.Last("Root.Tail"), Is.EqualTo((byte)7));
    }

    [Test]
    public void ByteLength_PublishedValue_IsResolved()
    {
        // the first string publishes its length (characters) under 'a'; the second takes it as its byte length
        var first = Ref("string", ("terminator", "0"));
        var published = new SchemaProcessorRef { Processor = first.Processor, PublishNamespace = "a" };
        published.PropertyList.AddRange(first.PropertyList);
        var root = Group("Root", [], Field("A", "sys.string", [published]), Text("B", ("byteLength", "pub:a.length")));

        var sink = Read(Build(root), Bytes("abc", 0, "xyz"));

        Assert.That(sink.Last("Root.B"), Is.EqualTo("xyz"));
    }

    [Test]
    public void ByteLength_InARepeat_UsesTheSameItem()
    {
        var root = Group("Root", [], Repeat("Rows", 2, Field("Len", "sys.uint8"), Text("Name", ("byteLength", "ref:^.Len"))));

        var sink = Read(Build(root), Bytes(1, "a", 2, "bc"));

        Assert.That(sink.All("Root.Rows.Name"), Is.EqualTo(new[] { "a", "bc" }));
    }

    [Test]
    public void ByteLength_AbsoluteReferenceWithIndex_UsesThatItem()
    {
        var root = Group("Root", [],
            Repeat("Lens", 2, Field("L", "sys.uint8")),
            Repeat("Names", 2, Text("Name", ("byteLength", "ref:Root.Lens[1].L"))));

        var sink = Read(Build(root), Bytes(1, 2, "ab", "cd"));

        Assert.That(sink.All("Root.Names.Name"), Is.EqualTo(new[] { "ab", "cd" }));
    }

    [Test]
    public void ProcessorDef_Reference_IsRelativeToTheNodeThatUsesIt()
    {
        var definition = new SchemaProcessorDef { Name = "sized", Processor = new ProcessorKey("sys", "string") };
        definition.PropertyList.Add(new SchemaProperty { Name = "sys.string.byteLength", Value = "ref:^.Len" });
        var use = new SchemaProcessorRef { Processor = new SchemaName("sized"), Definition = definition };
        var root = Group("Root", [],
            Group("A", [], Field("Len", "sys.uint8"), Field("Name", "sys.string", [use])),
            Group("B", [], Field("Len", "sys.uint8"), Field("Name", "sys.string", [use])));

        var sink = Read(Build(root), Bytes(1, "x", 2, "yz"));

        Assert.That(sink.Last("Root.A.Name"), Is.EqualTo("x"));
        Assert.That(sink.Last("Root.B.Name"), Is.EqualTo("yz"));
    }

    [Test]
    public void UndeclaredProperty_Reference_IsResolvedPerItem()
    {
        // a map entry (not a declared property of sys.map) maps 1 to the value of 'Alt' of the same item: no stale cache
        var map = Ref("map", ("to", "sys.int32"));
        map.PropertyList.Add(new SchemaProperty { Name = "1", Value = "ref:^.Alt" });
        var root = Group("Root", [], Repeat("Rows", 2, Field("Alt", "sys.uint8"), Field("Code", "sys.uint8", [map])));

        var sink = Read(Build(root), Bytes(42, 1, 7, 1));

        Assert.That(sink.All("Root.Rows.Code"), Is.EqualTo(new object[] { 42, 7 }));
    }

    [Test]
    public void Reference_ToAnUnknownNode_FailsTheBuild()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => Build(Group("Root", [], Text("Name", ("byteLength", "ref:^.Nope")))));

        Assert.That(ex!.Errors, Has.Some.Contains("does not match any node"));
    }

    [Test]
    public void Reference_ToAGroup_FailsTheBuild()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() =>
            Build(Group("Root", [], Group("G", [], Field("X", "sys.uint8")), Text("Name", ("byteLength", "ref:^.G")))));

        Assert.That(ex!.Errors, Has.Some.Contains("must refer to a field"));
    }

    [Test]
    public void Reference_ToAFieldOfAnotherType_FailsTheBuild()
    {
        var blob = new SchemaField { Name = "Blob", DataType = "sys.bytes", ByteLength = 2 };

        var ex = Assert.Throws<ExecutionPlanException>(() => Build(Group("Root", [], blob, Text("Name", ("byteLength", "ref:^.Blob")))));

        Assert.That(ex!.Errors, Has.Some.Contains("does not convert"));
    }

    [Test]
    public void Reference_InvalidText_FailsTheBuild()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => Build(Group("Root", [], Field("Len", "sys.uint8"), Text("Name", ("byteLength", "ref:^Len")))));

        Assert.That(ex!.Errors, Has.Some.Contains("not a valid value reference"));
    }

    [Test]
    public void Reference_ToALaterField_FailsAtRuntimeNamingTheProperty()
    {
        var plan = Build(Group("Root", [], Text("Name", ("byteLength", "ref:^.Len")), Field("Len", "sys.uint8")));

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(Bytes("ab", 2)), new ListSink()),
            Throws.InstanceOf<SerializationException>().With.Message.Contains("sys.string.byteLength").And.Message.Contains("not published"));
    }

    /// <summary>Records every field value in order (per schema path, all repeat items).</summary>
    private sealed class ListSink : IValueSink
    {
        private readonly List<(string Path, object? Value)> _values;

        public ListSink() : this([]) { }
        private ListSink(List<(string, object?)> values) { _values = values; }

        public object?[] All(string path) => [.. _values.Where(v => v.Path == path).Select(v => v.Value)];
        public object? Last(string path) => All(path)[^1];

        public void SetField(FieldContext context, LogicalField value) => _values.Add((context.Path, value.Value));
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }
}
