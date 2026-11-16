using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>
/// A choice with members but without a selectedIndex is open: the value model selects one of its members through
/// <see cref="IOpenChoiceSink"/>/<see cref="IOpenChoiceSource"/>, on read and write alike.
/// </summary>
public class OpenChoiceMemberTests
{
    private const string MemberSchema = """
        {
          "name": "MemberTest",
          "nodeDefs": [
            {
              "name": "Entry", "kind": "group",
              "members": [
                { "name": "A", "kind": "field", "datatype": "sys.uint8" },
                {
                  "name": "Part", "kind": "group",
                  "members": [
                    { "name": "P1", "kind": "field", "datatype": "sys.uint8" },
                    { "name": "P2", "kind": "field", "datatype": "sys.uint8" }
                  ]
                }
              ]
            }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Head", "kind": "field", "datatype": "sys.uint8" },
                {
                  "name": "Sel", "kind": "choice",
                  "members": [
                    { "name": "first", "kind": "group", "nodeDef": "Entry" },
                    { "name": "second", "kind": "group", "nodeDef": "Entry" },
                    { "name": "flag", "kind": "field", "datatype": "sys.uint8" },
                    { "name": "list", "kind": "repeat", "count": 2, "members": [ { "name": "L", "kind": "field", "datatype": "sys.uint8" } ] },
                    {
                      "name": "pick", "kind": "choice", "selectedIndex": 1,
                      "members": [ { "name": "p0", "kind": "field", "datatype": "sys.uint8" }, { "name": "p1", "kind": "field", "datatype": "sys.uint8" } ]
                    },
                    {
                      "name": "inner", "kind": "choice",
                      "members": [ { "name": "i0", "kind": "field", "datatype": "sys.uint8" }, { "name": "i1", "kind": "field", "datatype": "sys.uint8" } ]
                    }
                  ]
                },
                { "name": "Tail", "kind": "field", "datatype": "sys.uint8" }
              ]
            }
          ]
        }
        """;

    // the choice is inside a repeat and its member holds a repeat: targets with instances
    private const string InstanceSchema = """
        {
          "name": "InstanceTest",
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                {
                  "name": "Items", "kind": "repeat", "count": 2,
                  "members": [
                    {
                      "name": "Sel", "kind": "choice",
                      "members": [
                        { "name": "x", "kind": "group", "members": [ { "name": "Vals", "kind": "repeat", "count": 3, "members": [ { "name": "V", "kind": "field", "datatype": "sys.uint8" } ] } ] },
                        { "name": "y", "kind": "field", "datatype": "sys.uint8" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static readonly SchemaName Root = new("MemberTest.Root");

    [Test]
    public void Read_GroupMember_IsEnteredOnTheScopeOfTheChoice()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, _) => OpenChoiceSinkResult.Select(new LogSink("member", log), new SchemaName("second")));

        var read = CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 2, 3, 4, 9]), sink);

        Assert.That(read, Is.EqualTo(5));
        Assert.That(log, Is.EqualTo(new[]
        {
            "root:Root.Head=1",
            "member:enter Root.Sel.second",
            "member:Root.Sel.second.A=2",
            "member:enter Root.Sel.second.Part",
            "member:Root.Sel.second.Part.P1=3",
            "member:Root.Sel.second.Part.P2=4",
            "member:exit Root.Sel.second.Part",
            "member:exit Root.Sel.second",
            "member:exit choice Root.Sel",
            "root:Root.Tail=9",
            "root:exit Root",
        }));
    }

    [Test]
    public void Read_OpenChoice_GivesTheContextWithoutMemberNodeDef()
    {
        var contexts = new List<OpenChoiceContext>();
        var sink = new LogSink("root", [], (context, self) =>
        {
            contexts.Add(context);
            return OpenChoiceSinkResult.Select(self, new SchemaName("flag"));
        });

        CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 7, 9]), sink);

        var context = contexts.Single();
        Assert.That((context.Path, context.MemberNodeDef, context.Choice.IsOpen), Is.EqualTo(("Root.Sel", (SchemaName?)null, true)));
    }

    [Test]
    public void Read_FieldMember_IsSetOnTheScopeOfTheChoice()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, _) => OpenChoiceSinkResult.Select(new LogSink("member", log), new SchemaName("flag")));

        CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 7, 9]), sink);

        Assert.That(log, Is.EqualTo(new[] { "root:Root.Head=1", "member:Root.Sel.flag=7", "member:exit choice Root.Sel", "root:Root.Tail=9", "root:exit Root" }));
    }

    [Test]
    public void Read_RepeatMember_IsEnteredOnTheScopeOfTheChoice()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, _) => OpenChoiceSinkResult.Select(new LogSink("member", log), new SchemaName("list")));

        CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 5, 6, 9]), sink);

        Assert.That(log, Is.EqualTo(new[]
        {
            "root:Root.Head=1",
            "member:repeat Root.Sel.list[2]",
            "member:Root.Sel.list.L[0]=5",
            "member:Root.Sel.list.L[1]=6",
            "member:exit repeat Root.Sel.list[2]",
            "member:exit choice Root.Sel",
            "root:Root.Tail=9",
            "root:exit Root",
        }));
    }

    [Test]
    public void Read_ChoiceMember_IsEnteredOnTheScopeOfTheChoice()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, _) => OpenChoiceSinkResult.Select(new LogSink("member", log), new SchemaName("pick")));

        CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 7, 9]), sink);

        // the member choice is entered with its index before its fields are read
        Assert.That(log, Is.EqualTo(new[]
        {
            "root:Root.Head=1",
            "member:choice Root.Sel.pick[1]",
            "member:Root.Sel.pick.p1=7",
            "member:exit choice Root.Sel.pick",
            "member:exit choice Root.Sel",
            "root:Root.Tail=9",
            "root:exit Root",
        }));
    }

    [Test]
    public void Read_OpenChoiceMember_SelectsItsMemberOnTheScopeOfTheChoice()
    {
        var log = new List<string>();
        var inner = new LogSink("inner", log, (_, self) => OpenChoiceSinkResult.Select(self, new SchemaName("i1")));
        var sink = new LogSink("root", log, (_, _) => OpenChoiceSinkResult.Select(inner, new SchemaName("inner")));

        CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 7, 9]), sink);

        Assert.That(log, Is.EqualTo(new[]
        {
            "root:Root.Head=1",
            "inner:Root.Sel.inner.i1=7",
            "inner:exit choice Root.Sel.inner",
            "inner:exit choice Root.Sel",
            "root:Root.Tail=9",
            "root:exit Root",
        }));
    }

    [Test]
    public void Write_ChoiceMember_IsEnteredOnTheScopeOfTheChoice()
    {
        var output = new ArrayBufferWriter<byte>();
        var member = new MapSource(new() { ["Root.Sel.pick.p1"] = 7 });
        var source = new MapSource(new() { ["Root.Head"] = 1, ["Root.Tail"] = 9 }, _ => OpenChoiceSourceResult.Select(member, new SchemaName("pick")));

        CreateSerializer(MemberSchema).Serialize(Root, source, output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 7, 9 }));
        Assert.That(member.Entered, Is.EqualTo(new[] { "choice Root.Sel.pick", "exit choice Root.Sel.pick", "exit choice Root.Sel" }));
    }

    [Test]
    public void Write_GroupMember_IsEnteredOnTheScopeOfTheChoice()
    {
        var output = new ArrayBufferWriter<byte>();
        var member = new MapSource(new() { ["Root.Sel.first.A"] = 2, ["Root.Sel.first.Part.P1"] = 3, ["Root.Sel.first.Part.P2"] = 4 });
        var source = new MapSource(new() { ["Root.Head"] = 1, ["Root.Tail"] = 9 }, _ => OpenChoiceSourceResult.Select(member, new SchemaName("first")));

        CreateSerializer(MemberSchema).Serialize(Root, source, output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 9 }));
        Assert.That(member.Entered, Is.EqualTo(new[]
        {
            "Root.Sel.first", "Root.Sel.first.Part", "exit Root.Sel.first.Part", "exit Root.Sel.first", "exit choice Root.Sel",
        }));
    }

    [Test]
    public void Write_FieldMember_IsWrittenFromTheScopeOfTheChoice()
    {
        var output = new ArrayBufferWriter<byte>();
        var member = new MapSource(new() { ["Root.Sel.flag"] = 7 });
        var source = new MapSource(new() { ["Root.Head"] = 1, ["Root.Tail"] = 9 }, _ => OpenChoiceSourceResult.Select(member, new SchemaName("flag")));

        CreateSerializer(MemberSchema).Serialize(Root, source, output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 7, 9 }));
    }

    [Test]
    public void ReadWrite_RoundTrip_WithTheSameSelection()
    {
        var serializer = CreateSerializer(MemberSchema);
        var values = new Dictionary<string, long>();
        var sink = new StoreSink(values, (_, self) => OpenChoiceSinkResult.Select(self, new SchemaName("second")));

        serializer.Deserialize(Root, new ReadOnlySequence<byte>([1, 2, 3, 4, 9]), sink);
        var output = new ArrayBufferWriter<byte>();
        var source = new MapSource(values, self => OpenChoiceSourceResult.Select(self, new SchemaName("second")));
        serializer.Serialize(Root, source, output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 9 }));
    }

    [Test]
    [TestCase("nope")]
    [TestCase("MemberTest.second")]
    public void Read_UnknownMember_ThrowsNamingTheChoiceAndTheMember(string member)
    {
        var sink = new LogSink("root", [], (_, self) => OpenChoiceSinkResult.Select(self, new SchemaName(member)));

        var ex = Assert.Throws<SerializationException>(
            () => CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 2, 3, 4, 9]), sink));

        Assert.That(ex!.Message, Does.Contain("Root.Sel").And.Contain(member).And.Contain("first, second, flag, list, pick, inner"));
    }

    [Test]
    public void Read_SinkWithoutInterface_ThrowsNamingThePath()
    {
        var ex = Assert.Throws<SerializationException>(
            () => CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 7, 9]), new PlainSink()));

        Assert.That(ex!.Message, Does.Contain("Root.Sel").And.Contain(nameof(IOpenChoiceSink)));
    }

    [Test]
    public void Write_SourceWithoutInterface_ThrowsNamingThePath()
    {
        var source = new PlainSource(new MapSource(new() { ["Root.Head"] = 1 }));

        var ex = Assert.Throws<SerializationException>(
            () => CreateSerializer(MemberSchema).Serialize(Root, source, new ArrayBufferWriter<byte>()));

        Assert.That(ex!.Message, Does.Contain("Root.Sel").And.Contain(nameof(IOpenChoiceSource)));
    }

    [Test]
    public void Read_TargetInAGroupMember_ReadsOnlyTheTarget()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, self) =>
            OpenChoiceSinkResult.SelectNode(self, new SchemaName("second"), new SchemaPath("second.Part")));

        var read = CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 3, 4, 9]), sink);

        Assert.That(read, Is.EqualTo(4));
        Assert.That(log.Where(entry => entry.Contains('=')), Is.EqualTo(new[]
        {
            "root:Root.Head=1", "root:Root.Sel.second.Part.P1=3", "root:Root.Sel.second.Part.P2=4", "root:Root.Tail=9",
        }));
    }

    [Test]
    public void Write_RangeInAGroupMember_WritesOnlyTheRange()
    {
        var output = new ArrayBufferWriter<byte>();
        var source = new MapSource(new() { ["Root.Head"] = 1, ["Root.Sel.first.A"] = 2, ["Root.Sel.first.Part.P1"] = 3, ["Root.Sel.first.Part.P2"] = 4, ["Root.Tail"] = 9 },
            self => OpenChoiceSourceResult.SelectRange(self, new SchemaName("first"), new SchemaPath("first.A"), default, new SchemaPath("first.Part.P1"), default));

        CreateSerializer(MemberSchema).Serialize(Root, source, output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 9 }));
    }

    [Test]
    public void Read_TargetNotInTheMember_ThrowsNamingTheTarget()
    {
        var sink = new LogSink("root", [], (_, self) => OpenChoiceSinkResult.SelectNode(self, new SchemaName("second"), new SchemaPath("first.Part")));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 3, 4, 9]), sink));

        Assert.That(ex!.Message, Does.Contain("Root.Sel").And.Contain("first.Part"));
    }

    [Test]
    public void Read_TargetInAFieldMember_Throws()
    {
        var sink = new LogSink("root", [], (_, self) => OpenChoiceSinkResult.SelectNode(self, new SchemaName("flag"), new SchemaPath("flag")));

        var ex = Assert.Throws<SerializationException>(
            () => CreateSerializer(MemberSchema).Deserialize(Root, new ReadOnlySequence<byte>([1, 7, 9]), sink));

        Assert.That(ex!.Message, Does.Contain("Root.Sel").And.Contain("'flag' is a field"));
    }

    [Test]
    public void Read_RangeThatBeginsInsideTheChoice_AppliesToTheMember()
    {
        var serializer = CreateSerializer(MemberSchema);
        var range = serializer.GetPlan(Root).CreateRange(new SchemaPath("Root.Sel.second.Part"), new SchemaPath("Root.Tail"));
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, self) => OpenChoiceSinkResult.Select(self, new SchemaName("second")));

        serializer.Deserialize(range, new ReadOnlySequence<byte>([3, 4, 9]), sink);

        Assert.That(log.Where(entry => entry.Contains('=')), Is.EqualTo(new[]
        {
            "root:Root.Sel.second.Part.P1=3", "root:Root.Sel.second.Part.P2=4", "root:Root.Tail=9",
        }));
    }

    [Test]
    [TestCase("Root.Sel.second.Part", "Root.Tail")]
    [TestCase("Root.Head", "Root.Sel.second.Part")]
    public void Read_TargetWhenTheRangeBeginsOrEndsInsideTheChoice_Throws(string begin, string end)
    {
        var serializer = CreateSerializer(MemberSchema);
        var range = serializer.GetPlan(Root).CreateRange(new SchemaPath(begin), new SchemaPath(end));
        var sink = new LogSink("root", [], (_, self) => OpenChoiceSinkResult.SelectNode(self, new SchemaName("second"), new SchemaPath("second.Part")));

        var ex = Assert.Throws<SerializationException>(
            () => serializer.Deserialize(range, new ReadOnlySequence<byte>([1, 3, 4, 9]), sink));

        Assert.That(ex!.Message, Does.Contain("Root.Sel").And.Contain("begins or ends inside the open choice"));
    }

    [Test]
    public void Read_TargetWhenTheRangeCoversTheChoice_IsAllowed()
    {
        var serializer = CreateSerializer(MemberSchema);
        var range = serializer.GetPlan(Root).CreateRange(new SchemaPath("Root.Sel"));
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, self) => OpenChoiceSinkResult.SelectNode(self, new SchemaName("second"), new SchemaPath("second.A")));

        serializer.Deserialize(range, new ReadOnlySequence<byte>([2]), sink);

        Assert.That(log.Where(entry => entry.Contains('=')), Is.EqualTo(new[] { "root:Root.Sel.second.A=2" }));
    }

    [Test]
    public void Read_TargetWithInstance_IsInsideTheMemberOfEachItem()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (_, self) =>
            OpenChoiceSinkResult.SelectNode(self, new SchemaName("x"), new SchemaPath("x.Vals"), new InstancePath([1])));

        CreateSerializer(InstanceSchema).Deserialize(new SchemaName("InstanceTest.Root"), new ReadOnlySequence<byte>([5, 6]), sink);

        Assert.That(log.Where(entry => entry.Contains('=')), Is.EqualTo(new[]
        {
            "root:Root.Items.Sel.x.Vals.V[0][1]=5", "root:Root.Items.Sel.x.Vals.V[1][1]=6",
        }));
    }

    [Test]
    public void Read_MemberPerItem_IsSelectedForEachItem()
    {
        var log = new List<string>();
        var sink = new LogSink("root", log, (context, self) => context.Instance[0] == 0
            ? OpenChoiceSinkResult.Select(self, new SchemaName("y"))
            : OpenChoiceSinkResult.Select(self, new SchemaName("x")));

        CreateSerializer(InstanceSchema).Deserialize(new SchemaName("InstanceTest.Root"), new ReadOnlySequence<byte>([1, 2, 3, 4]), sink);

        Assert.That(log.Where(entry => entry.Contains('=')), Is.EqualTo(new[]
        {
            "root:Root.Items.Sel.y[0]=1",
            "root:Root.Items.Sel.x.Vals.V[1][0]=2", "root:Root.Items.Sel.x.Vals.V[1][1]=3", "root:Root.Items.Sel.x.Vals.V[1][2]=4",
        }));
    }

    [Test]
    public void Compile_ValueProcessorsWithoutSelectedIndex_Throws()
    {
        var json = MemberSchema.Replace(
            "\"name\": \"Sel\", \"kind\": \"choice\",",
            "\"name\": \"Sel\", \"kind\": \"choice\", \"valueProcessors\": [ { \"name\": \"sys.map\", \"properties\": [ { \"name\": \"a\", \"value\": \"1\" } ] } ],");
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.LoadFromJson(json);

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex!.Message, Does.Contain("MemberTest.Root.Sel").And.Contain("valueProcessors"));
    }

    [Test]
    public void Plan_ChoiceWithSelectedIndex_IsNotOpen()
    {
        var json = MemberSchema.Replace("\"name\": \"Sel\", \"kind\": \"choice\",", "\"name\": \"Sel\", \"kind\": \"choice\", \"selectedIndex\": 2,");

        var choice = (ChoiceInfo)CreateSerializer(json).GetPlan(Root).Find(new SchemaPath("Root.Sel"))!;

        Assert.That((choice.IsOpen, choice.MemberNodeDef), Is.EqualTo((false, (SchemaName?)null)));
    }

    private static Serializer CreateSerializer(string json)
    {
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.LoadFromJson(json);
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        return new SerializerBuilder()
            .AddSchemas(schemaBuilder.Compile())
            .AddProcessors(manager)
            .Build();
    }

    // logs the fields (with their instance), the groups entered and exited, and selects the member of an open choice
    private sealed class LogSink(string name, List<string> log, Func<OpenChoiceContext, LogSink, OpenChoiceSinkResult>? select = null) : IOpenChoiceSink
    {
        public void SetField(FieldContext context, LogicalField value) => log.Add($"{name}:{context.Path}{context.Instance}={value.Value}");
        public IValueSink EnterGroup(GroupContext context)
        {
            log.Add($"{name}:enter {context.Path}");
            return this;
        }
        public IValueSink EnterRepeat(RepeatContext context, int count)
        {
            log.Add($"{name}:repeat {context.Path}[{count}]");
            return this;
        }
        public void ExitRepeat(RepeatContext context, int count) => log.Add($"{name}:exit repeat {context.Path}[{count}]");
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex)
        {
            log.Add($"{name}:choice {context.Path}[{selectedIndex}]");
            return this;
        }
        public void ExitGroup(GroupContext context) => log.Add($"{name}:exit {context.Path}");
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) => log.Add($"{name}:exit choice {context.Path}");

        public OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context)
            => select?.Invoke(context, this) ?? throw new InvalidOperationException("No selection.");
    }

    // keeps the values by path and instance and selects the member of an open choice
    private sealed class StoreSink(Dictionary<string, long> values, Func<OpenChoiceContext, StoreSink, OpenChoiceSinkResult> select) : IOpenChoiceSink
    {
        public void SetField(FieldContext context, LogicalField value) => values[$"{context.Path}{context.Instance}"] = Convert.ToInt64(value.Value);
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
        public OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context) => select(context, this);
    }

    // a sink without IOpenChoiceSink
    private sealed class PlainSink : IValueSink
    {
        public void SetField(FieldContext context, LogicalField value) { }
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }

    // provides the values by path and instance; selects the member of an open choice; logs the groups it enters
    private sealed class MapSource(Dictionary<string, long> values, Func<MapSource, OpenChoiceSourceResult>? select = null) : IOpenChoiceSource
    {
        public List<string> Entered { get; } = [];

        public SourceResult GetField(FieldContext context)
            => values.TryGetValue($"{context.Path}{context.Instance}", out var value)
                ? SourceResult.Provided(new LogicalField(context.Name, typeof(byte), (byte)value))
                : SourceResult.NoValue();
        public IValueSource EnterGroup(GroupContext context)
        {
            Entered.Add(context.Path);
            return this;
        }
        public void ExitGroup(GroupContext context) => Entered.Add($"exit {context.Path}");
        public IValueSource EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) => Entered.Add($"exit choice {context.Path}");
        public IValueSource EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSource EnterChoice(ChoiceContext context, int selectedIndex)
        {
            Entered.Add($"choice {context.Path}");
            return this;
        }

        public OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context)
            => select?.Invoke(this) ?? throw new InvalidOperationException("No selection.");
    }

    // a source without IOpenChoiceSource
    private sealed class PlainSource(IValueSource inner) : IValueSource
    {
        public SourceResult GetField(FieldContext context) => inner.GetField(context);
        public IValueSource EnterGroup(GroupContext context) => this;
        public void ExitGroup(GroupContext context) { }
        public IValueSource EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
        public IValueSource EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSource EnterChoice(ChoiceContext context, int selectedIndex) => this;
    }
}
