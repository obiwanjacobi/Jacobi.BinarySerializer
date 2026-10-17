using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Editing;
using Jacobi.BinarySerializer.Yaml;
using JsonSerializer = Jacobi.BinarySerializer.Schema.Json.JsonSerializer;
using XmlSerializer = Jacobi.BinarySerializer.Schema.Xml.XmlSerializer;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class EditNodeTests
{
  [TestCase("json")]
  [TestCase("xml")]
  [TestCase("yaml")]
  public void RoundTrip_ConcreteDefinitions_PreserveTypedSettingsAndPipelinePresence(string format)
  {
    var original = new SchemaDocumentBuilder("Doc")
      .NodeDefs(definitions => definitions
        .Field("Value", "sys.bytes", value: "240", byteLength: new SchemaPubRef { Namespace = "header", Name = "length" }, byteOffset: -1)
        .Group("Body", members => members.Field("Code"), byteSize: 6)
        .Repeat("Rows", new SchemaNodeRef { Path = "Root.Count" }, members => members.Field("Row"), byteSize: 8,
          valueProcessors: processors => processors.Add("sys.map"))
        .Choice("Variant", 1, members => members.Group("Alternative"), byteSize: 4,
          valueProcessors: processors => processors.Add("sys.map"))
        .PackedGroup("Flags", 2, members => members.Field("Bit"), byteSize: 3)
        .OpenChoice("Open", "Body", SchemaPipelineMode.Isolate))
      .Group("Root", members => members
        .Field("Count")
        .Choice("Inherited", default, nodeDef: "Open")
        .Choice("Explicit", default, nodeDef: "Open", pipeline: SchemaPipelineMode.Integrate))
      .Build();
    var edit = EditNodeMapper.ToEditDocument(original);
    Assert.That(edit.NodeDefs.Select(node => node.GetType()), Is.EqualTo(new[]
    {
      typeof(EditField), typeof(EditGroup), typeof(EditRepeat), typeof(EditChoice), typeof(EditPackedGroup), typeof(EditChoice)
    }));
    var snapshot = new SchemaDocumentBuilder(edit).Build();
    var reloaded = format switch
    {
      "xml" => XmlSerializer.Deserialize(XmlSerializer.Serialize(snapshot)),
      "yaml" => YamlSerializer.Deserialize(YamlSerializer.Serialize(snapshot)),
      _ => JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot))
    };
    Assert.That(JsonSerializer.Serialize(reloaded), Is.EqualTo(JsonSerializer.Serialize(original)));
    Assert.That(reloaded.NodeDefs.Select(node => node.Kind), Is.EqualTo(new[]
    {
      SchemaNodeKind.Field, SchemaNodeKind.Group, SchemaNodeKind.Repeat,
      SchemaNodeKind.Choice, SchemaNodeKind.PackedGroup, SchemaNodeKind.Choice
    }));
    var field = (SchemaField)reloaded.NodeDefs[0];
    Assert.That(field.ByteLength is SchemaPubRef length && length.ToString() == "pub:header.length", Is.True);
    Assert.That(field.ByteOffset, Is.EqualTo(-1));
    var builder = new SchemaSetBuilder();
    builder.AddDocument(reloaded);
    builder.Compile();
    var choices = reloaded.Roots.Single().Members.OfType<SchemaChoice>().ToArray();
    Assert.That(choices[0].Pipeline, Is.EqualTo(SchemaPipelineMode.Isolate));
    Assert.That(choices[1].Pipeline, Is.EqualTo(SchemaPipelineMode.Integrate));
  }

    [TestCase("json")]
    [TestCase("xml")]
    [TestCase("yaml")]
    public void RoundTrip_PreservesAuthoredSchema(string format)
    {
        var original = CreateDocument();
        var edit = EditNodeMapper.ToEditDocument(original);
        var snapshot = new SchemaDocumentBuilder(edit).Build();

        Assert.That(JsonSerializer.Serialize(snapshot), Is.EqualTo(JsonSerializer.Serialize(original)));
        Assert.That(XmlSerializer.Serialize(snapshot), Is.EqualTo(XmlSerializer.Serialize(original)));
        var reloaded = format switch
        {
          "xml" => XmlSerializer.Deserialize(XmlSerializer.Serialize(snapshot)),
          "yaml" => YamlSerializer.Deserialize(YamlSerializer.Serialize(snapshot)),
          _ => JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot))
        };
        Assert.That(JsonSerializer.Serialize(reloaded), Is.EqualTo(JsonSerializer.Serialize(original)));
        Assert.That(snapshot.Roots.Single(), Is.SameAs(snapshot.Members.Single()));
        Assert.That(snapshot.Groups.Select(group => group.Name), Is.EqualTo(new[] { "Root", "Flags", "Items", "UntilEnd", "Variant", "Body" }));
        Assert.That(snapshot.Fields.Select(field => field.Name), Is.EqualTo(new[] { "Status", "Payload", "Mode", "Entry", "Remainder" }));
        Assert.That(snapshot.IsCompiled, Is.False);
    }

    [Test]
    public void Edits_DoNotMutateOriginalOrEarlierSnapshots()
    {
        var original = CreateDocument();
        var edit = EditNodeMapper.ToEditDocument(original);
        var root = (EditGroup)edit.Members.Single();
        var payload = (EditField)root.Members[1];
        var before = new SchemaDocumentBuilder(edit).Build();

        payload.Name = "Changed";
        payload.Processors.Single().Properties.Single().Value = "changed";
        root.Properties.Single().Value = "changed";
        edit.DataTypeDefs.Single().Options![1] = "Changed";
        edit.Includes.Single().Path = "changed.json";
        root.Members.RemoveAt(0);

        Assert.That(JsonSerializer.Serialize(before), Is.EqualTo(JsonSerializer.Serialize(original)));
        var after = EditNodeMapper.ToSchemaDocument(edit);
        Assert.That(after.Fields.First().Name, Is.EqualTo("Changed"));
        Assert.That(after.DataTypeDefs.Single().Options![1], Is.EqualTo("Changed"));
        Assert.That(after.Includes.Single().Path, Is.EqualTo("changed.json"));
        Assert.That(original.DataTypeDefs.Single().Options![1], Is.EqualTo("One"));
    }

    [Test]
    public void Builder_ImportsSchemaAndProducesIndependentBuilds()
    {
        var original = CreateDocument();
        var builder = new SchemaDocumentBuilder(original);
        var first = builder.Build();
        builder.Group("Extra", group => group.Field("Value"));
        var second = builder.Build();

        Assert.That(first.Members, Has.Count.EqualTo(1));
        Assert.That(second.Members, Has.Count.EqualTo(2));
        Assert.That(original.Members, Has.Count.EqualTo(1));
        Assert.That(first.Members[0], Is.Not.SameAs(second.Members[0]));
        Assert.That(first.NodeDefs.Single(), Is.Not.SameAs(second.NodeDefs.Single()));
        Assert.That(first.Includes.Single(), Is.Not.SameAs(second.Includes.Single()));
    }

    [Test]
    public void Builder_ExpressesVirtualFieldsOpenChoicesAndBoundedRepeats()
    {
        var document = new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions => definitions.Group("Body"))
            .SizedGroup("Envelope", 20, group => group
                .VirtualField("Status", "sys.uint8", -1)
                .RepeatToEnd("Data", members => members.Field("Value"), byteSize: 8)
                .OpenChoice("Body", "Body", SchemaPipelineMode.Isolate))
            .Build();

        var root = document.Roots.Single();
        Assert.That(root.ByteSize is int size && size == 20, Is.True);
        Assert.That(((SchemaField)root.Members[0]).ByteOffset, Is.EqualTo(-1));
        var repeat = (SchemaRepeat)root.Members[1];
        Assert.That(repeat.Count is int or SchemaNodeRef or SchemaPubRef, Is.False);
        Assert.That(repeat.ByteSize is int repeatSize && repeatSize == 8, Is.True);
        var choice = (SchemaChoice)root.Members[2];
        Assert.That(choice.MemberNodeDef?.ToString(), Is.EqualTo("Body"));
        Assert.That(choice.Pipeline, Is.EqualTo(SchemaPipelineMode.Isolate));
        Assert.That(choice.Members, Is.Empty);
    }

    [Test]
    public void Builder_PreservesReferenceOptionsAndValueProcessors()
    {
      var reference = new SchemaNodeRef { Path = "Root.Length" };
      var document = new SchemaDocumentBuilder("Doc")
        .Group("Root", group => group
          .Field("Value", "sys.bytes", value: "F0", byteLength: reference, byteOffset: -2)
          .PackedGroup("Flags", byteSize: 3)
          .Repeat("Items", reference, valueProcessors: processors => processors.Add("sys.map"), byteSize: 8)
          .Choice("Variant", 0, valueProcessors: processors => processors.Add("sys.map")), byteSize: reference)
        .Build();

      var root = document.Roots.Single();
      Assert.That(root.ByteSize is SchemaNodeRef size && size.Path == reference.Path, Is.True);
      var field = document.Fields.Single();
      Assert.That(field.Value is string value && value == "F0", Is.True);
      Assert.That(field.ByteLength is SchemaNodeRef length && length.Path == reference.Path, Is.True);
      Assert.That(field.ByteOffset, Is.EqualTo(-2));
      var packed = (SchemaPackedGroup)root.Members[1];
      Assert.That(packed.ByteLength is int or SchemaNodeRef or SchemaPubRef, Is.False);
      Assert.That(packed.ByteSize is int packedSize && packedSize == 3, Is.True);
      var repeat = (SchemaRepeat)root.Members[2];
      Assert.That(repeat.ValueProcessors.Single().Processor.ToString(), Is.EqualTo("sys.map"));
      Assert.That(repeat.ByteSize is int repeatSize && repeatSize == 8, Is.True);
      var choice = (SchemaChoice)root.Members[3];
      Assert.That(choice.ValueProcessors.Single().Processor.ToString(), Is.EqualTo("sys.map"));
      Assert.That(choice.SelectedIndex is int index && index == 0, Is.True);
    }

    [TestCase("240")]
    [TestCase("true")]
    [TestCase("null")]
    [TestCase("~")]
    [TestCase("1.5")]
    [TestCase("")]
    public void YamlRoundTrip_PreservesStringConstants(string value)
    {
      var document = new SchemaDocumentBuilder("Doc")
        .Group("Root", group => group.Field("Value", "sys.string", value: value))
        .Build();
      var reloaded = YamlSerializer.Deserialize(YamlSerializer.Serialize(document));
      Assert.That(reloaded.Fields.Single().Value is string constant && constant == value, Is.True);
    }

    [Test]
    public void NodeRoundTrip_PreservesUnsetPackedLengthAndReferenceIndices()
    {
        var packed = new EditPackedGroup { Name = "Flags" };
        var packedSnapshot = (SchemaPackedGroup)EditNodeMapper.ToSchemaNode(packed);
        Assert.That(packedSnapshot.ByteLength is int or SchemaNodeRef or SchemaPubRef, Is.False);
        Assert.That(packedSnapshot.EffectiveByteLength is int length && length == 1, Is.True);

        Assert.That(SchemaNodeRef.TryParse("ref:Root.Items[].Payload[2].Length", out var reference), Is.True);
        var field = new EditField { Name = "Payload", ByteLength = reference! };
        var snapshot = (SchemaField)EditNodeMapper.ToSchemaNode(field);
        var restored = (EditField)EditNodeMapper.ToEditNode(snapshot);
        Assert.That(restored.ByteLength is SchemaNodeRef result && result.ToString() == reference!.ToString(), Is.True);
        Assert.That(snapshot.ByteLength is SchemaNodeRef copy && !ReferenceEquals(copy, reference), Is.True);
    }

    [Test]
    public void Snapshot_PreservesDocumentSettingsAndTypedReferences()
    {
      var edit = new EditDocument { Name = "Doc", NodeDef = new SchemaName("Base"), ByteSize = 20 };
      edit.Properties.Add(new EditProperty { Name = "custom", Value = null });
      edit.Processors.Add(new EditProcessorRef { Processor = new SchemaName("sys.align"), PublishNamespace = "doc" });
      var group = new EditGroup { Name = "Root" };
      group.Members.Add(new EditField
      {
        Name = "Value", Value = new SchemaNodeRef { Path = "Root.Status" },
        ByteLength = new SchemaPubRef { Namespace = "header", Name = "size" }
      });
      edit.Members.Add(group);

      var snapshot = new SchemaDocumentBuilder(edit).Build();
      Assert.That(snapshot.NodeDef?.ToString(), Is.EqualTo("Base"));
      Assert.That(snapshot.ByteSize is int size && size == 20, Is.True);
      Assert.That(snapshot.Properties.Single().Value, Is.Null);
      Assert.That(snapshot.Processors.Single().PublishNamespace, Is.EqualTo("doc"));
      Assert.That(snapshot.Fields.Single().Value is SchemaNodeRef reference && reference.Path == "Root.Status", Is.True);
      Assert.That(snapshot.Fields.Single().ByteLength is SchemaPubRef length && length.ToString() == "pub:header.size", Is.True);
    }

    [Test]
    public void CompiledDocuments_AreNotImportedAsAuthoringState()
    {
        var document = CreateDocument();
        document.IsCompiled = true;
        Assert.Throws<ArgumentException>(() => EditNodeMapper.ToEditDocument(document));
        Assert.Throws<ArgumentException>(() => new SchemaDocumentBuilder(document));
    }

    private static SchemaDocument CreateDocument()
        => JsonSerializer.Deserialize("""
            {
              "name": "Doc",
              "properties": [{ "name": "description", "value": "Document" }],
              "includes": [{ "schema": "Other", "path": "other.json" }],
              "nodeDefs": [
                { "name": "BodyType", "kind": "group", "properties": [{ "name": "extra", "value": null }],
                  "processors": [{ "name": "sys.align", "pubns": "body", "alignment": "2" }],
                  "members": [{ "name": "DefinitionField", "kind": "field", "datatype": "sys.uint8" }] }
              ],
              "dataTypeDefs": [
                { "name": "Scaled", "basedOn": "sys.int16", "min": -10, "max": 100, "scale": 2, "shift": 1,
                  "options": { "1": "One" }, "properties": [{ "name": "note", "value": null }],
                  "processors": [{ "name": "sys.scale", "pubns": "scaled" }] }
              ],
              "processorDefs": [{ "name": "map", "processor": "sys.map", "properties": [{ "name": "other", "value": null }] }],
              "members": [
                { "name": "Root", "kind": "group", "byteSize": { "pub": "header.size" },
                  "properties": [{ "name": "note", "value": "root" }],
                  "processors": [{ "name": "sys.align", "pubns": "root", "alignment": "4" }],
                  "members": [
                    { "name": "Status", "kind": "field", "datatype": "sys.uint8", "value": "240", "byteOffset": 0 },
                    { "name": "Payload", "kind": "field", "nodeDef": "Other.Payload", "datatype": "sys.bytes",
                      "value": { "pub": "header.value" }, "byteLength": { "ref": "Root.Items[].Length" },
                      "processors": [{ "name": "ref:map", "pubns": "payload", "extra": "value" }] },
                    { "name": "Flags", "kind": "packedGroup", "byteLength": 2, "byteSize": 3,
                      "processors": [{ "name": "sys.bitpacker", "bitorder": "little" }],
                      "members": [{ "name": "Mode", "kind": "field", "datatype": "sys.uint8" }] },
                    { "name": "Items", "kind": "repeat", "count": { "ref": "Root.Status" }, "byteSize": 12,
                      "valueProcessors": [{ "name": "ref:map", "pubns": "count" }],
                      "members": [{ "name": "Entry", "kind": "field", "datatype": "Scaled", "byteLength": 2 }] },
                    { "name": "UntilEnd", "kind": "repeat", "byteSize": 4,
                      "members": [{ "name": "Remainder", "kind": "field", "datatype": "sys.uint8" }] },
                    { "name": "Variant", "kind": "choice", "selectedIndex": { "pub": "header.index" },
                      "valueProcessors": [{ "name": "ref:map" }],
                      "members": [{ "name": "Body", "kind": "choice", "memberNodeDef": "BodyType", "pipeline": "isolate" }] }
                  ] }
              ]
            }
            """);
}