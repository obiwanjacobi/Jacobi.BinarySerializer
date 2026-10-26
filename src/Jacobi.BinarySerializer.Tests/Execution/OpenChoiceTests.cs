using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Json;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class OpenChoiceTests
{
    // An open choice has no alternatives (and no selectedIndex): the value model selects the group.
    private const string OpenChoiceSchema = """
        {
          "name": "OpenTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ]
            },
            {
              "name": "BodyB", "kind": "group", "nodeDef": "Body",
              "members": [ { "name": "Value", "kind": "field", "datatype": "sys.uint8" } ]
            }
          ]
        }
        """;

    [Test]
    public void Read_OpenChoice_QueriesTheSinkForTheGroup()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var sink = new OpenSink();

        serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x07]), sink);

        Assert.That(sink.Contexts, Has.Count.EqualTo(1));
        Assert.That(sink.Contexts[0].Path, Is.EqualTo("Root.Msg"));
        Assert.That(sink.Contexts[0].MemberNodeDef?.ToString(), Is.EqualTo("Body"));
        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Id=7" }));
    }

    [Test]
    public void Read_OpenChoice_UnknownGroup_Throws()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var sink = new OpenSink { Selected = new SchemaName("Nope") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x01, 0x07]), sink));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("Nope"));
    }

    [Test]
    public void Read_OpenChoice_QualifiedGroup_IsEntered()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var sink = new OpenSink { Selected = new SchemaName("OpenTest.BodyB") };

        serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x09]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyB.Value=9" }));
    }

    [Test]
    public void Read_OpenChoice_SinkWithoutInterface_ThrowsNamingThePath()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x01]), new PlainSink()));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain(nameof(IOpenChoiceSink)));
    }

    [Test]
    public void Write_OpenChoice_QueriesTheSourceForTheGroup()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var source = new OpenSource();
        var output = new ArrayBufferWriter<byte>();

        serializer.Serialize(new SchemaName("OpenTest.Root"), source, output);

        Assert.That(source.Contexts, Has.Count.EqualTo(1));
        Assert.That(source.Contexts[0].Path, Is.EqualTo("Root.Msg"));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0x05 }));
    }

    [Test]
    public void Write_OpenChoice_SourceWithoutInterface_ThrowsNamingThePath()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Serialize(new SchemaName("OpenTest.Root"), new PlainSource(), new ArrayBufferWriter<byte>()));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain(nameof(IOpenChoiceSource)));
    }

    [Test]
    public void Compile_OpenChoiceWithMissingNodeDef_IsUnresolved()
    {
        var json = OpenChoiceSchema.Replace("\"memberNodeDef\": \"Body\"", "\"memberNodeDef\": \"Missing\"");
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(JsonSerializer.Deserialize(json));

        Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());
    }

    [Test]
    public void Compile_OpenChoiceWithNonGroupNodeDef_Throws()
    {
        var json = OpenChoiceSchema.Replace("\"name\": \"Body\", \"kind\": \"group\"", "\"name\": \"Body\", \"kind\": \"field\"");
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(JsonSerializer.Deserialize(json));

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex.Message, Does.Contain("'OpenTest.Root.Msg'").And.Contain("must be a Group"));
    }

    [Test]
    public void Json_OpenChoice_RoundTripsMemberNodeDef()
    {
        var document = JsonSerializer.Deserialize(OpenChoiceSchema);

        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(document));

        var root = (SchemaGroup)roundTripped.Members.First();
        var choice = (SchemaChoice)root.Members.Single();
        Assert.That(choice.MemberNodeDef?.ToString(), Is.EqualTo("Body"));
    }

    private const string HeaderSchema = """
        {
          "name": "HeaderTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Head", "kind": "field", "datatype": "sys.uint8" },
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ]
            }
          ]
        }
        """;

    [Test]
    public void Read_GroupRange_IncludesTheOpenChoice()
    {
        var serializer = CreateSerializer(HeaderSchema);
        var range = serializer.GetPlan(new SchemaName("HeaderTest.Root")).CreateRange(new SchemaPath("Root"));
        var sink = new OpenSink();

        var read = serializer.Deserialize(range, new ReadOnlySequence<byte>([0x01, 0x07]), sink);

        Assert.That(sink.Contexts, Has.Count.EqualTo(1));
        Assert.That(sink.Fields, Is.EqualTo(new[] { "Root.Head=1", "BodyA.Id=7" }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Read_FieldRangeBeforeTheOpenChoice_DoesNotEnterIt()
    {
        var serializer = CreateSerializer(HeaderSchema);
        var range = serializer.GetPlan(new SchemaName("HeaderTest.Root")).CreateRange(new SchemaPath("Root.Head"), new SchemaPath("Root.Head"));
        var sink = new OpenSink();

        var read = serializer.Deserialize(range, new ReadOnlySequence<byte>([0x01, 0x07]), sink);

        Assert.That(sink.Contexts, Is.Empty);
        Assert.That(sink.Fields, Is.EqualTo(new[] { "Root.Head=1" }));
        Assert.That(read, Is.EqualTo(1));
    }

    [Test]
    public void Read_OpenChoiceWithoutConformingGroup_Throws()
    {
        var json = OpenChoiceSchema
            .Replace("\"name\": \"BodyA\", \"kind\": \"group\", \"nodeDef\": \"Body\"", "\"name\": \"BodyA\", \"kind\": \"group\"")
            .Replace("\"name\": \"BodyB\", \"kind\": \"group\", \"nodeDef\": \"Body\"", "\"name\": \"BodyB\", \"kind\": \"group\"");
        var serializer = CreateSerializer(json);

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x07]), new OpenSink()));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("Body").And.Contain("conforms"));
    }

    private const string NestedSchema = """
        {
          "name": "NestedTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Pre", "kind": "field", "datatype": "sys.uint8" },
                {
                  "name": "Inner",
                  "kind": "group",
                  "members": [
                    { "name": "Flag", "kind": "field", "datatype": "sys.uint8" },
                    { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
                  ]
                }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [
                { "name": "X", "kind": "field", "datatype": "sys.uint8" },
                { "name": "Y", "kind": "field", "datatype": "sys.uint8" }
              ]
            }
          ]
        }
        """;

    [Test]
    public void Read_RangeOverGroupWithOpenChoice_ReadsAllBodyFields()
    {
        var serializer = CreateSerializer(NestedSchema);
        var range = serializer
            .GetPlan(new SchemaName("NestedTest.Root"))
            .CreateRange(new SchemaPath("Root.Inner"));
        var sink = new OpenSink();

        serializer.Deserialize(range, new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "Root.Inner.Flag=1", "BodyA.X=2", "BodyA.Y=3" }));
    }

    private const string TargetSchema = """
        {
          "name": "TargetTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [
                { "name": "Skip", "kind": "field", "datatype": "sys.uint8" },
                {
                  "name": "Part", "kind": "group",
                  "members": [
                    { "name": "P1", "kind": "field", "datatype": "sys.uint8" },
                    { "name": "P2", "kind": "field", "datatype": "sys.uint8" }
                  ]
                },
                { "name": "Tail", "kind": "field", "datatype": "sys.uint8" }
              ]
            }
          ]
        }
        """;

    [Test]
    public void Read_OpenChoiceTarget_ReadsOnlyTheTargetGroup()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Target = new SchemaPath("BodyA.Part") };

        var read = serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Part.P1=1", "BodyA.Part.P2=2" }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Read_OpenChoiceTargetWithoutGroup_ResolvesTheBodyFromTheTarget()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Selected = default, Target = new SchemaPath("BodyA.Part") };

        var read = serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Part.P1=1", "BodyA.Part.P2=2" }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Read_OpenChoiceWithoutTarget_ReadsTheWholeBody()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink();

        serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03, 0x04]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(4));
    }

    [Test]
    public void Read_OpenChoiceTargetNotInBody_Throws()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Target = new SchemaPath("BodyA.Nope") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("BodyA.Nope"));
    }

    private const string TargetInstanceSchema = """
        {
          "name": "TargetInstanceTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [
                { "name": "Head", "kind": "field", "datatype": "sys.uint8" },
                {
                  "name": "Items", "kind": "repeat", "count": 3,
                  "members": [
                    {
                      "name": "Part", "kind": "group",
                      "members": [
                        { "name": "P1", "kind": "field", "datatype": "sys.uint8" },
                        { "name": "P2", "kind": "field", "datatype": "sys.uint8" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Test]
    public void Read_OpenChoiceTargetInstance_ReadsOnlyThatItem()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var sink = new OpenSink { Target = new SchemaPath("BodyA.Items.Part"), TargetInstance = new InstancePath([1]) };

        var read = serializer.Deserialize(new SchemaName("TargetInstanceTest.Root"), new ReadOnlySequence<byte>([0x07, 0x08]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Items.Part.P1=7", "BodyA.Items.Part.P2=8" }));
        Assert.That(sink.Instances, Is.EqualTo(new[] { new InstancePath([1]), new InstancePath([1]) }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Read_OpenChoiceTargetWithoutInstance_ReadsAllItems()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var sink = new OpenSink { Target = new SchemaPath("BodyA.Items.Part") };

        var read = serializer.Deserialize(new SchemaName("TargetInstanceTest.Root"), new ReadOnlySequence<byte>([1, 2, 3, 4, 5, 6]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(6));
        Assert.That(read, Is.EqualTo(6));
    }

    [Test]
    public void Write_OpenChoiceTargetInstance_WritesOnlyThatItem()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var source = new OpenSource { Target = new SchemaPath("BodyA.Items.Part"), TargetInstance = new InstancePath([2]) };
        var writer = new ArrayBufferWriter<byte>();

        serializer.Serialize(new SchemaName("TargetInstanceTest.Root"), source, writer);

        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 5 }));
        Assert.That(source.Instances, Is.EqualTo(new[] { new InstancePath([2]), new InstancePath([2]) }));
    }

    private const string OpenRepeatBodySchema = """
        {
          "name": "RepeatTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [
                {
                  "name": "Items", "kind": "repeat",
                  "members": [
                    { "name": "A", "kind": "field", "datatype": "sys.uint8" },
                    { "name": "B", "kind": "field", "datatype": "sys.uint8" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Test]
    public void Read_OpenRepeatInBody_StopsWhenLessThanAnItemRemains()
    {
        var serializer = CreateSerializer(OpenRepeatBodySchema);
        var sink = new OpenSink();

        var read = serializer.Deserialize(new SchemaName("RepeatTest.Root"), new ReadOnlySequence<byte>([1, 2, 3, 4, 5]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(4));
        Assert.That(read, Is.EqualTo(4));
    }

    private const string RepeatOfOpenChoiceSchema = """
        {
          "name": "RepeatChoiceTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                {
                  "name": "Items", "kind": "repeat",
                  "members": [
                    { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
                  ]
                }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ]
            }
          ]
        }
        """;

    [Test]
    public void Read_OpenRepeatOfOpenChoice_ReadsAllItems()
    {
        var serializer = CreateSerializer(RepeatOfOpenChoiceSchema);
        var sink = new OpenSink();

        var read = serializer.Deserialize(new SchemaName("RepeatChoiceTest.Root"), new ReadOnlySequence<byte>([1, 2, 3]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(3));
        Assert.That(read, Is.EqualTo(3));
    }

    [Test]
    public void Read_OpenRepeatOfSizedOpenChoice_ReadsAllItems()
    {
        var serializer = CreateSerializer(RepeatOfOpenChoiceSchema.Replace("\"memberNodeDef\": \"Body\"", "\"memberNodeDef\": \"Body\", \"byteSize\": 1"));
        var sink = new OpenSink();

        var read = serializer.Deserialize(new SchemaName("RepeatChoiceTest.Root"), new ReadOnlySequence<byte>([1, 2, 3]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(3));
        Assert.That(read, Is.EqualTo(3));
    }

    [Test]
    public void Read_SizedOpenRepeatOfRecordsInBody_ReadsAllRecords()
    {
        var json = OpenRepeatBodySchema.Replace(
            "\"name\": \"Items\", \"kind\": \"repeat\",",
            "\"name\": \"Items\", \"kind\": \"repeat\", \"byteSize\": 4,");
        var serializer = CreateSerializer(json);
        var sink = new OpenSink();

        var read = serializer.Deserialize(new SchemaName("RepeatTest.Root"), new ReadOnlySequence<byte>([1, 2, 3, 4]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(4));
        Assert.That(read, Is.EqualTo(4));
    }

    private const string PipelineSchema = """
        {
          "name": "PipeTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "processors": [ { "name": "sys.align", "bytes": 2 } ],
              "members": [
                { "name": "Head", "kind": "field", "datatype": "sys.uint8" },
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ]
            }
          ]
        }
        """;

    [Test]
    public void Read_OpenChoiceIntegrate_BodyInheritsTheLayoutOfTheChoice()
    {
        var serializer = CreateSerializer(PipelineSchema.Replace("\"memberNodeDef\": \"Body\"", "\"memberNodeDef\": \"Body\", \"pipeline\": \"integrate\""));
        var sink = new OpenSink();

        serializer.Deserialize(new SchemaName("PipeTest.Root"), new ReadOnlySequence<byte>([5, 0, 7, 0]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "Root.Head=5", "BodyA.Id=7" }));
    }

    [Test]
    public void Read_OpenChoiceIsolate_BodyHasItsOwnPipeline()
    {
        var serializer = CreateSerializer(PipelineSchema.Replace("\"memberNodeDef\": \"Body\"", "\"memberNodeDef\": \"Body\", \"pipeline\": \"isolate\""));
        var sink = new OpenSink();

        serializer.Deserialize(new SchemaName("PipeTest.Root"), new ReadOnlySequence<byte>([5, 0, 7, 0]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "Root.Head=5", "BodyA.Id=0" }));
    }

    [Test]
    public void Json_OpenChoice_RoundTripsPipeline()
    {
        var json = PipelineSchema.Replace("\"memberNodeDef\": \"Body\"", "\"memberNodeDef\": \"Body\", \"pipeline\": \"integrate\"");

        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(JsonSerializer.Deserialize(json)));

        var root = (SchemaGroup)roundTripped.Members.First();
        Assert.That(((SchemaChoice)root.Members.Last()).Pipeline, Is.EqualTo(SchemaPipelineMode.Integrate));
    }

    [Test]
    public void Compile_PipelineOnClosedChoice_Throws()
    {
        var json = """
            {
              "name": "ClosedTest",
              "members": [
                { "name": "Root", "kind": "group", "members": [
                  { "name": "Pick", "kind": "choice", "selectedIndex": 0, "pipeline": "isolate",
                    "members": [ { "name": "A", "kind": "field", "datatype": "sys.uint8" } ] }
                ] }
              ]
            }
            """;
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(JsonSerializer.Deserialize(json));

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex.Message, Does.Contain("Pick").And.Contain("open choice"));
    }

    private static Serializer CreateSerializer(string json)
    {
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.LoadFromJson(json);
        var schemaSet = schemaBuilder.Compile();

        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());

        return new SerializerBuilder()
            .AddSchemas(schemaSet)
            .AddProcessors(manager)
            .UseModelForValueFields()
            .Build();
    }

    private class PlainSink : IValueSink
    {
        public List<string> Fields { get; } = [];
        public List<InstancePath> Instances { get; } = [];
        public void SetField(FieldContext context, LogicalField value)
        {
            Fields.Add($"{context.Path}={value.Value}");
            Instances.Add(context.Instance);
        }
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }

    private sealed class OpenSink : PlainSink, IOpenChoiceSink
    {
        public List<OpenChoiceContext> Contexts { get; } = [];
        public SchemaName Selected { get; init; } = new("BodyA");
        public SchemaPath Target { get; init; }
        public InstancePath TargetInstance { get; init; }

        public OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context)
        {
            Contexts.Add(context);
            return new OpenChoiceSinkResult(this, Selected, Target, TargetInstance);
        }
    }

    private class PlainSource : IValueSource
    {
        public List<InstancePath> Instances { get; } = [];
        public SourceResult GetField(FieldContext context)
        {
            Instances.Add(context.Instance);
            return SourceResult.Provided(new LogicalField(context.Name, typeof(byte), (byte)5));
        }
        public IValueSource EnterGroup(GroupContext context) => this;
        public int GetCount(RepeatContext context) => throw new NotSupportedException();
        public IValueSource EnterItem(RepeatContext context, int index) => this;
        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => this;
    }

    private sealed class OpenSource : PlainSource, IOpenChoiceSource
    {
        public List<OpenChoiceContext> Contexts { get; } = [];
        public SchemaPath Target { get; init; }
        public InstancePath TargetInstance { get; init; }

        public OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context)
        {
            Contexts.Add(context);
            return new OpenChoiceSourceResult(this, new SchemaName("BodyA"), Target, TargetInstance);
        }
    }
}
