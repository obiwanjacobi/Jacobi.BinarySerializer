using System.Buffers;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Json;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class NodeDefTests
{
    // The fields only reference the nodeDef: the data type comes from the nodeDef.
    private const string FieldNodeDefSchema = """
        {
          "name": "NodeDefTest",
          "nodeDefs": [
            { "name": "Word", "kind": "field", "datatype": "sys.uint16" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "processors": [ { "name": "sys.bytepacker", "byteorder": "big" } ],
              "members": [
                { "name": "A", "kind": "field", "nodeDef": "Word" },
                { "name": "B", "kind": "field", "nodeDef": "Word" }
              ]
            }
          ]
        }
        """;

    [Test]
    public void Read_FieldsUsingNodeDef_UseTheNodeDefDataType()
    {
        var serializer = CreateSerializer(FieldNodeDefSchema);
        var sink = new Sink();

        serializer.Deserialize(new SchemaName("NodeDefTest.Root"), new ReadOnlySequence<byte>([0x00, 0x01, 0x01, 0x02]), sink);

        Assert.That(sink.Values.Keys, Is.EquivalentTo(new[] { "Root.A", "Root.B" }));
        Assert.That(Convert.ToInt32(sink.Values["Root.A"]), Is.EqualTo(1));
        Assert.That(Convert.ToInt32(sink.Values["Root.B"]), Is.EqualTo(0x0102));
    }

    [Test]
    public void Read_GroupUsingNodeDef_ConcatsGroupMembersToNodeDefMembers()
    {
        var json = """
            {
              "name": "NodeDefTest",
              "nodeDefs": [
                {
                  "name": "Header",
                  "kind": "group",
                  "members": [
                    { "name": "Id", "kind": "field", "datatype": "sys.uint8" }
                  ]
                }
              ],
              "members": [
                {
                  "name": "Root",
                  "kind": "group",
                  "processors": [ { "name": "sys.bytepacker", "byteorder": "big" } ],
                  "members": [
                    {
                      "name": "Msg",
                      "kind": "group",
                      "nodeDef": "Header",
                      "members": [
                        { "name": "Value", "kind": "field", "datatype": "sys.uint16" }
                      ]
                    }
                  ]
                }
              ]
            }
            """;
        var serializer = CreateSerializer(json);
        var sink = new Sink();

        serializer.Deserialize(new SchemaName("NodeDefTest.Root"), new ReadOnlySequence<byte>([0x07, 0x01, 0x02]), sink);

        Assert.That(sink.Values.Keys, Is.EqualTo(new[] { "Root.Msg.Id", "Root.Msg.Value" }));
        Assert.That(Convert.ToInt32(sink.Values["Root.Msg.Id"]), Is.EqualTo(7));
        Assert.That(Convert.ToInt32(sink.Values["Root.Msg.Value"]), Is.EqualTo(0x0102));
    }

    [Test]
    public void Compile_NodeDefOfOtherKind_Throws()
    {
        var json = """
            {
              "name": "NodeDefTest",
              "nodeDefs": [
                { "name": "Word", "kind": "group" }
              ],
              "members": [
                {
                  "name": "Root",
                  "kind": "group",
                  "members": [
                    { "name": "A", "kind": "field", "datatype": "sys.uint16", "nodeDef": "Word" }
                  ]
                }
              ]
            }
            """;
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(JsonSerializer.Deserialize(json));

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex.Message, Does.Contain("'NodeDefTest.Root.A'").And.Contain("'Word'").And.Contain("kinds must match"));
    }

    [Test]
    public void Compile_UnknownNodeDef_ReportsFullName()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CompileField("Nope"));

        Assert.That(ex.Message, Does.Contain("'NodeDefTest.Nope'"));
    }

    [Test]
    public void Compile_UnknownNodeDefInsideNodeDef_ReportsFullName()
    {
        var json = """
            {
              "name": "NodeDefTest",
              "nodeDefs": [
                { "name": "Header", "kind": "group", "members": [
                  { "name": "Id", "kind": "field", "datatype": "sys.uint8", "nodeDef": "Nope" }
                ] }
              ],
              "members": [
                { "name": "Root", "kind": "group", "members": [
                  { "name": "Msg", "kind": "group", "nodeDef": "Header" }
                ] }
              ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => Compile(json));

        Assert.That(ex.Message, Does.Contain("'NodeDefTest.Nope'"));
    }

    // A root that references a nodeDef: the members of the nodeDef first, then its own; its processors too.
    private static string RootNodeDefSchema(string rootProcessors = "") => $$"""
        {
          "name": "NodeDefTest",
          "nodeDefs": [
            {
              "name": "Message",
              "kind": "group",
              "processors": [ { "name": "sys.bytepacker", "byteorder": "big" } ],
              "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ]
            }
          ],
          "members": [
            {
              "name": "Msg",
              "kind": "group",
              "nodeDef": "Message",
              {{rootProcessors}}
              "members": [ { "name": "Value", "kind": "field", "datatype": "sys.uint16" } ]
            }
          ]
        }
        """;

    [Test]
    public void Read_RootUsingNodeDef_HasTheNodeDefMembersFirstAndItsProcessors()
    {
        var serializer = CreateSerializer(RootNodeDefSchema());
        var sink = new Sink();

        serializer.Deserialize(new SchemaName("NodeDefTest.Msg"), new ReadOnlySequence<byte>([0x07, 0x01, 0x02]), sink);

        Assert.That(sink.Values.Keys, Is.EqualTo(new[] { "Msg.Id", "Msg.Value" }));
        Assert.That(Convert.ToInt32(sink.Values["Msg.Id"]), Is.EqualTo(7));
        // big endian: the processor of the nodeDef
        Assert.That(Convert.ToInt32(sink.Values["Msg.Value"]), Is.EqualTo(0x0102));
    }

    [Test]
    public void Write_RootUsingNodeDef_HasTheNodeDefMembersFirstAndItsProcessors()
    {
        var serializer = CreateSerializer(RootNodeDefSchema());
        var output = new ArrayBufferWriter<byte>();

        serializer.Serialize(new SchemaName("NodeDefTest.Msg"), new Source(new() { ["Msg.Id"] = (byte)7, ["Msg.Value"] = (ushort)0x0102 }), output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0x07, 0x01, 0x02 }));
    }

    [Test]
    public void Read_RootUsingNodeDef_ItsOwnProcessorReplacesTheNodeDefProcessor()
    {
        var serializer = CreateSerializer(RootNodeDefSchema("""
            "processors": [ { "name": "sys.bytepacker", "byteorder": "little" } ],
            """));
        var sink = new Sink();

        serializer.Deserialize(new SchemaName("NodeDefTest.Msg"), new ReadOnlySequence<byte>([0x07, 0x01, 0x02]), sink);

        Assert.That(Convert.ToInt32(sink.Values["Msg.Value"]), Is.EqualTo(0x0201));
    }

    [Test]
    public void Read_NodeUsingNodeDef_AMemberWithTheSameNameReplacesTheNodeDefMemberInItsPlace()
    {
        var json = """
            {
              "name": "NodeDefTest",
              "nodeDefs": [
                {
                  "name": "Message",
                  "kind": "group",
                  "processors": [ { "name": "sys.bytepacker", "byteorder": "big" } ],
                  "members": [
                    { "name": "Id", "kind": "field", "datatype": "sys.uint8" },
                    { "name": "Value", "kind": "field", "datatype": "sys.uint8" }
                  ]
                }
              ],
              "members": [
                {
                  "name": "Msg",
                  "kind": "group",
                  "nodeDef": "Message",
                  "members": [ { "name": "ID", "kind": "field", "datatype": "sys.uint16" } ]
                }
              ]
            }
            """;
        var serializer = CreateSerializer(json);
        var sink = new Sink();

        serializer.Deserialize(new SchemaName("NodeDefTest.Msg"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink);

        Assert.That(sink.Values.Keys, Is.EqualTo(new[] { "Msg.ID", "Msg.Value" }));
        Assert.That(Convert.ToInt32(sink.Values["Msg.ID"]), Is.EqualTo(0x0102));
        Assert.That(Convert.ToInt32(sink.Values["Msg.Value"]), Is.EqualTo(3));
    }

    [Test]
    public void Compile_RootUsingNodeDefOfOtherKind_Throws()
    {
        var json = """
            {
              "name": "NodeDefTest",
              "nodeDefs": [
                { "name": "Word", "kind": "field", "datatype": "sys.uint16" }
              ],
              "members": [
                { "name": "Root", "kind": "group", "nodeDef": "Word" }
              ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => Compile(json));

        Assert.That(ex.Message, Does.Contain("'NodeDefTest.Root'").And.Contain("'Word'").And.Contain("kinds must match"));
    }

    [Test]
    public void Compile_RootUsingUnknownNodeDef_ReportsFullName()
    {
        var json = """
            {
              "name": "NodeDefTest",
              "members": [
                { "name": "Root", "kind": "group", "nodeDef": "Nope" }
              ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => Compile(json));

        Assert.That(ex.Message, Does.Contain("'NodeDefTest.Root'").And.Contain("'NodeDefTest.Nope'"));
    }

    private static void CompileField(string nodeDef)
        => Compile($$"""
            {
              "name": "NodeDefTest",
              "members": [
                { "name": "Root", "kind": "group", "members": [
                  { "name": "A", "kind": "field", "datatype": "sys.uint16", "nodeDef": "{{nodeDef}}" }
                ] }
              ]
            }
            """);

    private static void Compile(string json)
    {
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(JsonSerializer.Deserialize(json));
        schemaBuilder.Compile();
    }

    private static Serializer CreateSerializer(string json)
    {
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(JsonSerializer.Deserialize(json));
        var schemaSet = schemaBuilder.Compile();

        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());

        return new SerializerBuilder()
            .AddSchemas(schemaSet)
            .AddProcessors(manager)
            .UseModelForValueFields()
            .Build();
    }

    private sealed class Sink : IValueSink
    {
        public Dictionary<string, object?> Values { get; } = [];

        public void SetField(FieldContext context, LogicalField value) => Values[context.Path] = value.Value;
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }

    private sealed class Source(Dictionary<string, object> values) : IValueSource
    {
        public SourceResult GetField(FieldContext context)
            => SourceResult.Provided(new LogicalField(context.Name, values[context.Path].GetType(), values[context.Path]));
        public IValueSource EnterGroup(GroupContext context) => this;
        public void ExitGroup(GroupContext context) { }
        public IValueSource EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public IValueSource EnterItem(RepeatContext context, int index, int count) => this;
        public void ExitItem(RepeatContext context, int index, int count) { }
        public IValueSource EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }
}
