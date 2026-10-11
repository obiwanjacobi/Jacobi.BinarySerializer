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
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(JsonSerializer.Deserialize(json));

        var ex = Assert.Throws<InvalidOperationException>(() => schemaSet.Compile());

        Assert.That(ex.Message, Does.Contain("'A'").And.Contain("'Word'").And.Contain("kinds must match"));
    }

    private static Serializer CreateSerializer(string json)
    {
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(JsonSerializer.Deserialize(json));
        schemaSet.Compile();

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
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void Complete() { }
    }
}
