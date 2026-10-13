using System.Buffers;
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

        Assert.That(ex.Message, Does.Contain("'Msg'").And.Contain("must be a Group"));
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

    private class PlainSink : IValueSink
    {
        public List<string> Fields { get; } = [];
        public void SetField(FieldContext context, LogicalField value) => Fields.Add($"{context.Path}={value.Value}");
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void Complete() { }
    }

    private sealed class OpenSink : PlainSink, IOpenChoiceSink
    {
        public List<OpenChoiceContext> Contexts { get; } = [];
        public SchemaName Selected { get; init; } = new("BodyA");

        public OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context)
        {
            Contexts.Add(context);
            return new OpenChoiceSinkResult(this, Selected);
        }
    }

    private class PlainSource : IValueSource
    {
        public SourceResult GetField(FieldContext context) => SourceResult.Provided(new LogicalField(context.Name, typeof(byte), (byte)5));
        public IValueSource EnterGroup(GroupContext context) => this;
        public int GetCount(RepeatContext context) => throw new NotSupportedException();
        public IValueSource EnterItem(RepeatContext context, int index) => this;
        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => this;
    }

    private sealed class OpenSource : PlainSource, IOpenChoiceSource
    {
        public List<OpenChoiceContext> Contexts { get; } = [];

        public OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context)
        {
            Contexts.Add(context);
            return new OpenChoiceSourceResult(this, new SchemaName("BodyA"));
        }
    }
}
