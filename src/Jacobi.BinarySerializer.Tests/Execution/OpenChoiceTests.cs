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
    public void Read_OpenChoice_TheBodyIsEnteredOnTheScopeOfTheChoice()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var sink = new OpenSink();

        serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x07]), sink);

        Assert.That(sink.Events, Is.EqualTo(new[] { "enter BodyA", "exit BodyA", "exit choice Root.Msg[-1]", "exit Root" }));
    }

    [Test]
    public void Write_OpenChoice_TheBodyIsEnteredOnTheScopeOfTheChoice()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var source = new OpenSource();

        serializer.Serialize(new SchemaName("OpenTest.Root"), source, new ArrayBufferWriter<byte>());

        Assert.That(source.Events, Is.EqualTo(new[] { "enter BodyA", "exit BodyA", "exit choice Root.Msg[-1]", "exit Root" }));
    }

    [Test]
    public void Read_OpenChoice_UnknownGroup_Throws()
    {
        var serializer = CreateSerializer(OpenChoiceSchema);
        var sink = new OpenSink { Selected = new SchemaName("Nope") };

        var ex = Assert.Throws<SerializationException>(
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

        var ex = Assert.Throws<SerializationException>(
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

        var ex = Assert.Throws<SerializationException>(
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

        var ex = Assert.Throws<SerializationException>(
            () => serializer.Deserialize(new SchemaName("OpenTest.Root"), new ReadOnlySequence<byte>([0x07]), new OpenSink()));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("Body").And.Contain("conforms"));
    }

    // the body ends with a repeat without a count; the envelope continues after the open choice
    private const string TrailingSchema = """
        { "name": "TrailTest", "nodeDefs": [ { "name": "Body", "kind": "group" } ], "members": [
            { "name": "Root", "kind": "group", "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body" },
                { "name": "Tail", "kind": "field", "datatype": "sys.uint8" }
            ] },
            { "name": "BodyA", "kind": "group", "nodeDef": "Body", "members": [
                { "name": "Items", "kind": "repeat", "members": [ { "name": "V", "kind": "field", "datatype": "sys.uint8" } ] }
            ] }
        ] }
        """;

    [Test]
    public void Read_OpenRepeatAtEndOfBodyFollowedByEnvelopeNodes_RequiresOpenRepeatSink()
    {
        var serializer = CreateSerializer(TrailingSchema);

        var ex = Assert.Throws<SerializationException>(
            () => serializer.Deserialize(new SchemaName("TrailTest.Root"), new ReadOnlySequence<byte>([1, 2, 9]), new OpenSink()));

        Assert.That(ex.Message, Does.Contain("BodyA.Items").And.Contain(nameof(IOpenRepeatSink)));
    }

    [Test]
    public void Read_OpenRepeatAtEndOfBodyFollowedByEnvelopeNodes_DefaultThrows()
    {
        var serializer = CreateSerializer(TrailingSchema);
        var sink = new OpenRepeatOpenSink((_, _) => OpenRepeatAction.Default);

        var ex = Assert.Throws<SerializationException>(
            () => serializer.Deserialize(new SchemaName("TrailTest.Root"), new ReadOnlySequence<byte>([1, 2, 9]), sink));

        Assert.That(ex.Message, Does.Contain("BodyA.Items").And.Contain(nameof(OpenRepeatAction.Default)));
    }

    [Test]
    public void Read_OpenRepeatAtEndOfBodyFollowedByEnvelopeNodes_SinkEndsIt()
    {
        var serializer = CreateSerializer(TrailingSchema);
        var sink = new OpenRepeatOpenSink((_, index) => index < 2 ? OpenRepeatAction.Next : OpenRepeatAction.End);

        serializer.Deserialize(new SchemaName("TrailTest.Root"), new ReadOnlySequence<byte>([1, 2, 9]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Items.V=1", "BodyA.Items.V=2", "Root.Tail=9" }));
    }

    [Test]
    public void Read_OpenRepeatAtEndOfBodyOfLastOpenChoice_EndsAtInputEnd()
    {
        var serializer = CreateSerializer(TrailingSchema.Replace("{ \"name\": \"Tail\", \"kind\": \"field\", \"datatype\": \"sys.uint8\" }", String.Empty)
            .Replace("\"memberNodeDef\": \"Body\" },", "\"memberNodeDef\": \"Body\" }"));
        var sink = new OpenSink();

        serializer.Deserialize(new SchemaName("TrailTest.Root"), new ReadOnlySequence<byte>([1, 2, 9]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Items.V=1", "BodyA.Items.V=2", "BodyA.Items.V=9" }));
    }

    // the envelope names a contract of another document; only the body depends on that document
    private const string EnvelopeSchema = """
        { "name": "Envelope", "members": [
            { "name": "Root", "kind": "group", "members": [
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Contracts.Body" }
            ] }
        ] }
        """;

    [Test]
    public void Read_ContractInOtherDocument_EntersTheBody()
    {
        var serializer = CreateSerializer(
            EnvelopeSchema,
            """{ "name": "Contracts", "nodeDefs": [ { "name": "Body", "kind": "group" } ] }""",
            """
            { "name": "Device", "members": [
                { "name": "BodyA", "kind": "group", "nodeDef": "Contracts.Body",
                  "members": [ { "name": "Id", "kind": "field", "datatype": "sys.uint8" } ] }
            ] }
            """);
        var sink = new OpenSink { Selected = new SchemaName("Device.BodyA") };

        serializer.Deserialize(new SchemaName("Envelope.Root"), new ReadOnlySequence<byte>([0x07]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Id=7" }));
    }

    [Test]
    public void Read_ContractInDocumentThatIsNotLoaded_ThrowsNoConformingGroup()
    {
        var serializer = CreateSerializer(EnvelopeSchema);

        var ex = Assert.Throws<SerializationException>(
            () => serializer.Deserialize(new SchemaName("Envelope.Root"), new ReadOnlySequence<byte>([0x07]), new OpenSink()));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("Contracts.Body").And.Contain("conforms"));
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
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Part") };

        var read = serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Part.P1=1", "BodyA.Part.P2=2" }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Result_WithoutMember_Throws()
    {
        var sink = new PlainSink();

        Assert.Throws<ArgumentException>(() => OpenChoiceSinkResult.Select(sink, default));
        Assert.Throws<ArgumentException>(() => OpenChoiceSinkResult.SelectNode(sink, default, new SchemaPath("BodyA.Part")));
    }

    [Test]
    public void Result_WithoutTarget_Throws()
    {
        var sink = new PlainSink();

        Assert.Throws<ArgumentException>(() => OpenChoiceSinkResult.SelectNode(sink, new SchemaName("BodyA"), default));
        Assert.Throws<ArgumentException>(() => OpenChoiceSinkResult.SelectRange(sink, new SchemaName("BodyA"), default, default, new SchemaPath("BodyA.Tail"), default));
        Assert.Throws<ArgumentException>(() => OpenChoiceSinkResult.SelectRange(sink, new SchemaName("BodyA"), new SchemaPath("BodyA.Part"), default, default, default));
    }

    [Test]
    public void Result_SelectNode_EndsAtTheEndOfTheNode()
    {
        var result = OpenChoiceSinkResult.SelectNode(new PlainSink(), new SchemaName("BodyA"), new SchemaPath("BodyA.Part"), new InstancePath([1]));

        Assert.That((result.Begin.Value, result.BeginInstance, result.End.IsEmpty, result.EndInstance.IsEmpty),
            Is.EqualTo(("BodyA.Part", new InstancePath([1]), true, true)));
    }

    [Test]
    public void Result_WithScope_KeepsTheSelection()
    {
        var other = new PlainSink();
        var result = OpenChoiceSinkResult.SelectRange(new PlainSink(), new SchemaName("BodyA"),
            new SchemaPath("BodyA.Part"), new InstancePath([1]), new SchemaPath("BodyA.Tail"), default).WithScope(other);

        Assert.That(result.Scope, Is.SameAs(other));
        Assert.That((result.Member.Name, result.Begin.Value, result.BeginInstance, result.End.Value),
            Is.EqualTo(("BodyA", "BodyA.Part", new InstancePath([1]), "BodyA.Tail")));
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
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Nope") };

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
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Items.Part"), BeginInstance = new InstancePath([1]) };

        var read = serializer.Deserialize(new SchemaName("TargetInstanceTest.Root"), new ReadOnlySequence<byte>([0x07, 0x08]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Items.Part.P1=7", "BodyA.Items.Part.P2=8" }));
        Assert.That(sink.Instances, Is.EqualTo(new[] { new InstancePath([1]), new InstancePath([1]) }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Read_OpenChoiceTargetWithoutInstance_ReadsAllItems()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Items.Part") };

        var read = serializer.Deserialize(new SchemaName("TargetInstanceTest.Root"), new ReadOnlySequence<byte>([1, 2, 3, 4, 5, 6]), sink);

        Assert.That(sink.Fields, Has.Count.EqualTo(6));
        Assert.That(read, Is.EqualTo(6));
    }

    [Test]
    public void Write_OpenChoiceTargetInstance_WritesOnlyThatItem()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var source = new OpenSource { Begin = new SchemaPath("BodyA.Items.Part"), BeginInstance = new InstancePath([2]) };
        var writer = new ArrayBufferWriter<byte>();

        serializer.Serialize(new SchemaName("TargetInstanceTest.Root"), source, writer);

        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 5 }));
        Assert.That(source.Instances, Is.EqualTo(new[] { new InstancePath([2]), new InstancePath([2]) }));
    }

    [Test]
    public void Read_OpenChoiceFieldTarget_ReadsOnlyThatField()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Part.P2") };

        var read = serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x02, 0x03]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Part.P2=2" }));
        Assert.That(read, Is.EqualTo(1));
    }

    [Test]
    public void Read_OpenChoiceTargetRange_ReadsFromTheStartUpToTheEnd()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Part.P2"), End = new SchemaPath("BodyA.Tail") };

        var read = serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x02, 0x03]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Part.P2=2", "BodyA.Tail=3" }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Read_OpenChoiceTargetRangeOverItems_ReadsFromTheStartItemUpToTheEndItem()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var sink = new OpenSink
        {
            Begin = new SchemaPath("BodyA.Items.Part.P2"), BeginInstance = new InstancePath([0]),
            End = new SchemaPath("BodyA.Items.Part.P1"), EndInstance = new InstancePath([1]),
        };

        var read = serializer.Deserialize(new SchemaName("TargetInstanceTest.Root"), new ReadOnlySequence<byte>([0x08, 0x09]), sink);

        Assert.That(sink.Fields, Is.EqualTo(new[] { "BodyA.Items.Part.P2=8", "BodyA.Items.Part.P1=9" }));
        Assert.That(sink.Instances, Is.EqualTo(new[] { new InstancePath([0]), new InstancePath([1]) }));
        Assert.That(read, Is.EqualTo(2));
    }

    [Test]
    public void Write_OpenChoiceTargetRangeOverItems_WritesFromTheStartItemUpToTheEndItem()
    {
        var serializer = CreateSerializer(TargetInstanceSchema);
        var source = new OpenSource
        {
            Begin = new SchemaPath("BodyA.Items.Part"), BeginInstance = new InstancePath([1]),
            End = new SchemaPath("BodyA.Items.Part"), EndInstance = new InstancePath([2]),
        };
        var writer = new ArrayBufferWriter<byte>();

        serializer.Serialize(new SchemaName("TargetInstanceTest.Root"), source, writer);

        Assert.That(writer.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 5, 5, 5 }));
        Assert.That(source.Instances, Is.EqualTo(new[] { new InstancePath([1]), new InstancePath([1]), new InstancePath([2]), new InstancePath([2]) }));
    }

    [Test]
    public void Read_OpenChoiceTargetEndBeforeStart_Throws()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Tail"), End = new SchemaPath("BodyA.Part") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("BodyA.Tail").And.Contain("BodyA.Part"));
    }

    [Test]
    public void Read_OpenChoiceTargetEndNotInBody_Throws()
    {
        var serializer = CreateSerializer(TargetSchema);
        var sink = new OpenSink { Begin = new SchemaPath("BodyA.Part"), End = new SchemaPath("BodyA.Nope") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => serializer.Deserialize(new SchemaName("TargetTest.Root"), new ReadOnlySequence<byte>([0x01, 0x02, 0x03]), sink));

        Assert.That(ex.Message, Does.Contain("Root.Msg").And.Contain("BodyA.Nope"));
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

    // Msg sits between two fields, so a window or padding around the body shows in where Tail is read.
    private static string SandwichSchema(string choiceExtra, string nodeDefExtra = "") => $$"""
        {
          "name": "EnvTest",
          "nodeDefs": [
            { "name": "Body", "kind": "group" },
            { "name": "OpenMsg", "kind": "choice", "memberNodeDef": "Body"{{nodeDefExtra}} }
          ],
          "members": [
            {
              "name": "Root",
              "kind": "group",
              "members": [
                { "name": "Head", "kind": "field", "datatype": "sys.uint8" },
                { "name": "Msg", "kind": "choice", "memberNodeDef": "Body"{{choiceExtra}} },
                { "name": "Tail", "kind": "field", "datatype": "sys.uint8" }
              ]
            },
            {
              "name": "BodyA", "kind": "group", "nodeDef": "Body",
              "members": [
                { "name": "Id", "kind": "field", "datatype": "sys.uint8" },
                { "name": "X", "kind": "field", "datatype": "sys.uint8" }
              ]
            }
          ]
        }
        """;

    private static readonly string[] SandwichFields = ["Root.Head=5", "BodyA.Id=7", "BodyA.X=8", "Root.Tail=3"];

    [Test]
    public void Read_OpenChoiceByteSize_LimitsTheBodyToTheWindow()
    {
        var serializer = CreateSerializer(SandwichSchema(", \"byteSize\": 2"));
        var sink = new OpenSink();

        var read = serializer.Deserialize(new SchemaName("EnvTest.Root"), new ReadOnlySequence<byte>([5, 7, 8, 3]), sink);

        Assert.That(sink.Fields, Is.EqualTo(SandwichFields));
        Assert.That(read, Is.EqualTo(4));
    }

    [Test]
    public void Read_OpenChoiceByteSizeLargerThanTheBody_Throws()
    {
        var serializer = CreateSerializer(SandwichSchema(", \"byteSize\": 3"));

        var ex = Assert.Throws<SerializationException>(
            () => serializer.Deserialize(new SchemaName("EnvTest.Root"), new ReadOnlySequence<byte>([5, 7, 8, 9, 3]), new OpenSink()));

        Assert.That(ex!.Message, Does.Contain("Root.Msg"));
    }

    [Test]
    public void Write_OpenChoiceByteSize_ChecksTheEncodedBody()
    {
        var output = new ArrayBufferWriter<byte>();
        CreateSerializer(SandwichSchema(", \"byteSize\": 2")).Serialize(new SchemaName("EnvTest.Root"), new OpenSource(), output);
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 5, 5, 5 }));

        var mismatch = CreateSerializer(SandwichSchema(", \"byteSize\": 3"));
        var ex = Assert.Throws<SerializationException>(
            () => mismatch.Serialize(new SchemaName("EnvTest.Root"), new OpenSource(), new ArrayBufferWriter<byte>()));
        Assert.That(ex!.Message, Does.Contain("Root.Msg").And.Contain("does not match"));
    }

    [Test]
    public void Read_OpenChoiceProcessorsIntegrate_ApplyToTheBody()
    {
        var serializer = CreateSerializer(SandwichSchema(", \"processors\": [ { \"name\": \"sys.align\", \"bytes\": 2 } ]"));
        var sink = new OpenSink();

        // X is aligned within the body (padding byte at 2); no padding at the end of the body
        var read = serializer.Deserialize(new SchemaName("EnvTest.Root"), new ReadOnlySequence<byte>([5, 7, 0, 8, 3]), sink);

        Assert.That(sink.Fields, Is.EqualTo(SandwichFields));
        Assert.That(read, Is.EqualTo(5));
    }

    [Test]
    public void Write_OpenChoiceProcessorsIntegrate_ApplyToTheBody()
    {
        var output = new ArrayBufferWriter<byte>();

        CreateSerializer(SandwichSchema(", \"processors\": [ { \"name\": \"sys.align\", \"bytes\": 2 } ]"))
            .Serialize(new SchemaName("EnvTest.Root"), new OpenSource(), output);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 5, 0, 5, 5 }));
    }

    [TestCase(", \"pipeline\": \"isolate\", \"processors\": [ { \"name\": \"sys.align\", \"bytes\": 2 } ]", "isolate")]
    [TestCase(", \"selectedIndex\": 0", "selectedIndex")]
    [TestCase(", \"valueProcessors\": [ { \"name\": \"sys.map\", \"properties\": [ { \"name\": \"a\", \"value\": \"1\" } ] } ]", "valueProcessors")]
    [TestCase(", \"members\": [ { \"name\": \"A\", \"kind\": \"field\", \"datatype\": \"sys.uint8\" } ]", "members")]
    public void Compile_OpenChoiceWithUnusedProperty_Throws(string choiceExtra, string expected)
    {
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.LoadFromJson(SandwichSchema(choiceExtra));

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex!.Message, Does.Contain("EnvTest.Root.Msg").And.Contain(expected));
    }

    [Test]
    public void Compile_OpenChoiceNodeDefWithUnusedProperty_Throws()
    {
        var json = SandwichSchema("", ", \"selectedIndex\": 0")
            .Replace("{ \"name\": \"Msg\", \"kind\": \"choice\", \"memberNodeDef\": \"Body\" }", "{ \"name\": \"Msg\", \"kind\": \"choice\", \"nodeDef\": \"OpenMsg\" }");
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.LoadFromJson(json);

        var ex = Assert.Throws<InvalidOperationException>(() => schemaBuilder.Compile());

        Assert.That(ex!.Message, Does.Contain("EnvTest.OpenMsg").And.Contain("selectedIndex"));
    }

    private static Serializer CreateSerializer(params string[] jsons)
    {
        var schemaBuilder = new SchemaSetBuilder();
        foreach (var json in jsons)
        {
            schemaBuilder.LoadFromJson(json);
        }
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
        public List<string> Events { get; } = [];
        public List<string> Fields { get; } = [];
        public List<InstancePath> Instances { get; } = [];
        public void SetField(FieldContext context, LogicalField value)
        {
            Fields.Add($"{context.Path}={value.Value}");
            Instances.Add(context.Instance);
        }
        public IValueSink EnterGroup(GroupContext context)
        {
            Events.Add($"enter {context.Path}");
            return this;
        }
        public IValueSink EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) => Events.Add($"exit {context.Path}");
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) => Events.Add($"exit choice {context.Path}[{selectedIndex}]");
    }

    private sealed class OpenRepeatOpenSink(Func<OpenRepeatReadContext, int, OpenRepeatAction> next) : PlainSink, IOpenChoiceSink, IOpenRepeatSink
    {
        public OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context)
            => OpenChoiceSinkResult.Select(this, new SchemaName("BodyA"));

        public OpenRepeatSinkResult EnterOpenRepeat(OpenRepeatReadContext context) => OpenRepeatSinkResult.ItemByItem(this);

        public OpenRepeatAction NextItem(OpenRepeatReadContext context, int index) => next(context, index);
    }

    private sealed class OpenSink : PlainSink, IOpenChoiceSink
    {
        public List<OpenChoiceContext> Contexts { get; } = [];
        public SchemaName Selected { get; init; } = new("BodyA");
        public SchemaPath Begin { get; init; }
        public SchemaPath End { get; init; }
        public InstancePath BeginInstance { get; init; }
        public InstancePath EndInstance { get; init; }

        public OpenChoiceSinkResult EnterOpenChoice(OpenChoiceContext context)
        {
            Contexts.Add(context);
            return Begin.IsEmpty ? OpenChoiceSinkResult.Select(this, Selected)
                : End.IsEmpty ? OpenChoiceSinkResult.SelectNode(this, Selected, Begin, BeginInstance)
                : OpenChoiceSinkResult.SelectRange(this, Selected, Begin, BeginInstance, End, EndInstance);
        }
    }

    private class PlainSource : IValueSource
    {
        public List<string> Events { get; } = [];
        public List<InstancePath> Instances { get; } = [];
        public SourceResult GetField(FieldContext context)
        {
            Instances.Add(context.Instance);
            return SourceResult.Provided(new LogicalField(context.Name, typeof(byte), (byte)5));
        }
        public IValueSource EnterGroup(GroupContext context)
        {
            Events.Add($"enter {context.Path}");
            return this;
        }
        public void ExitGroup(GroupContext context) => Events.Add($"exit {context.Path}");
        public IValueSource EnterRepeat(RepeatContext context, int count) => this;
        public void ExitRepeat(RepeatContext context, int count) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) => Events.Add($"exit choice {context.Path}[{selectedIndex}]");
        public IValueSource EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSource EnterChoice(ChoiceContext context, int selectedIndex) => this;
    }

    private sealed class OpenSource : PlainSource, IOpenChoiceSource
    {
        public List<OpenChoiceContext> Contexts { get; } = [];
        public SchemaPath Begin { get; init; }
        public SchemaPath End { get; init; }
        public InstancePath BeginInstance { get; init; }
        public InstancePath EndInstance { get; init; }

        public OpenChoiceSourceResult EnterOpenChoice(OpenChoiceContext context)
        {
            Contexts.Add(context);
            var member = new SchemaName("BodyA");
            return Begin.IsEmpty ? OpenChoiceSourceResult.Select(this, member)
                : End.IsEmpty ? OpenChoiceSourceResult.SelectNode(this, member, Begin, BeginInstance)
                : OpenChoiceSourceResult.SelectRange(this, member, Begin, BeginInstance, End, EndInstance);
        }
    }
}
