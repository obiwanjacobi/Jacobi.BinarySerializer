using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using JsonSerializer = Jacobi.BinarySerializer.Schema.Json.JsonSerializer;
using XmlSerializer = Jacobi.BinarySerializer.Schema.Xml.XmlSerializer;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class PackedGroupSchemaTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_PreservesPackedGroupAndIndependentSizes(bool xml)
    {
        var builder = new SchemaSetBuilder();
        var document = xml
            ? builder.LoadFromXml("""
                <schema name="Doc">
                  <members>
                    <packedGroup name="Flags" byteLength="1" byteSize="3" extra="group">
                      <processors><processor name="sys.bitpacker" bitorder="little" pubns="flags" /></processors>
                      <members>
                        <field name="Mode" datatype="sys.uint8" sys.bitpacker.bits="3">
                          <processor name="sys.scale" scale="2" />
                        </field>
                        <field name="Channel" datatype="sys.uint8" value="7" sys.bitpacker.bits="5" />
                      </members>
                    </packedGroup>
                  </members>
                </schema>
                """)
            : builder.LoadFromJson("""
                { "name": "Doc", "members": [
                  { "name": "Flags", "kind": "packedGroup", "byteLength": 1, "byteSize": 3, "extra": "group",
                    "processors": [ { "name": "sys.bitpacker", "bitorder": "little", "pubns": "flags" } ],
                    "members": [
                      { "name": "Mode", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 3,
                        "processors": [ { "name": "sys.scale", "scale": 2 } ] },
                      { "name": "Channel", "kind": "field", "datatype": "sys.uint8", "value": "7", "sys.bitpacker.bits": 5 }
                    ] }
                ] }
                """);

        var roundTripped = xml
            ? XmlSerializer.Deserialize(XmlSerializer.Serialize(document))
            : JsonSerializer.Deserialize(JsonSerializer.Serialize(document));

        foreach (var schema in new[] { document, roundTripped })
        {
            Assert.That(schema.Roots.Single(), Is.TypeOf<SchemaPackedGroup>());
            var packed = (SchemaPackedGroup)schema.Roots.Single();
            Assert.That(packed.Kind, Is.EqualTo(SchemaNodeKind.PackedGroup));
            Assert.That(packed.ByteLength is int length && length == 1, Is.True);
            Assert.That(packed.ByteSize is int size && size == 3, Is.True);
            Assert.That(packed.Properties.Select(p => (p.Name, p.Value)), Is.EqualTo(new[] { ("extra", "group") }));
            Assert.That(packed.Processors.Single().Processor.FullName, Is.EqualTo("sys.bitpacker"));
            Assert.That(packed.Processors.Single().PublishNamespace, Is.EqualTo("flags"));
            Assert.That(packed.Processors.Single().Properties.Single().Value, Is.EqualTo("little"));
            Assert.That(packed.Members.Select(m => m.Name), Is.EqualTo(new[] { "Mode", "Channel" }));
            Assert.That(schema.Fields, Has.Count.EqualTo(2));
            var mode = (SchemaField)packed.Members[0];
            Assert.That(mode.DataType!.Value.FullName, Is.EqualTo("sys.uint8"));
            Assert.That(mode.Properties.Single().Name, Is.EqualTo("sys.bitpacker.bits"));
            Assert.That(mode.Properties.Single().Value, Is.EqualTo("3"));
            Assert.That(mode.Processors.Single().Processor.FullName, Is.EqualTo("sys.scale"));
            Assert.That(((SchemaField)packed.Members[1]).Value is string value && value == "7", Is.True);
        }
        Assert.That(builder.Compile().FindByPath<SchemaField>("Doc.Flags.Mode").Name, Is.EqualTo("Mode"));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public void RoundTrip_PreservesCarrierLengthReferences(bool xml, bool published)
    {
        var key = published ? "pub" : "ref";
        var path = published ? "header.length" : "Root.Length";
        var builder = new SchemaSetBuilder();
        var document = xml
            ? builder.LoadFromXml($"""
                <schema name="Doc"><members><group name="Root"><members>
                  <field name="Length" datatype="sys.uint8" />
                  <packedGroup name="Flags"><members><field name="Mode" datatype="sys.uint8" /></members>
                    <byteLength {key}="{path}" />
                  </packedGroup>
                </members></group></members></schema>
                """)
            : builder.LoadFromJson($$"""
                { "name": "Doc", "members": [ { "name": "Root", "kind": "group", "members": [
                  { "name": "Length", "kind": "field", "datatype": "sys.uint8" },
                  { "name": "Flags", "kind": "packedGroup", "byteLength": { "{{key}}": "{{path}}" },
                    "members": [ { "name": "Mode", "kind": "field", "datatype": "sys.uint8" } ] }
                ] } ] }
                """);
        var roundTripped = xml
            ? XmlSerializer.Deserialize(XmlSerializer.Serialize(document))
            : JsonSerializer.Deserialize(JsonSerializer.Serialize(document));
        foreach (var schema in new[] { document, roundTripped })
        {
            var packed = (SchemaPackedGroup)schema.Roots.Single().Members[1];
            if (published)
            {
                Assert.That(packed.ByteLength is SchemaPubRef reference && reference.ToString() == "pub:" + path, Is.True);
            }
            else
            {
                Assert.That(packed.ByteLength is SchemaNodeRef reference && reference.Path == path, Is.True);
            }
            Assert.That(packed.Properties, Is.Empty);
        }
        Assert.That(builder.Compile().FindByPath("Doc.Root.Flags"), Is.TypeOf<SchemaPackedGroup>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Compile_NodeDef_PreservesKindOrderPropertiesAndProcessorReferences(bool root)
    {
        var document = new SchemaDocumentBuilder("Doc")
            .ProcessorDef("packing", "test.packing", ("order", "little"))
            .DataTypeDef("Scaled", "sys.uint8", processors: p => p.Add("sys.scale"))
            .NodeDefs(definitions => definitions
              .Field("Mode", "Scaled")
              .PackedGroup("FlagsDef", members: group => group.Field("Mode", dataType: null, nodeDef: "Mode"),
                processors: processors => processors.Add("ref:packing")));
        if (root)
        {
            document.PackedGroup("Flags", 1, g => g.Field("Channel"), nodeDef: "FlagsDef");
        }
        else
        {
            document.Group("Root", g => g.PackedGroup("Flags", 1, f => f.Field("Channel"), nodeDef: "FlagsDef"));
        }
        var schema = document.Build();
        foreach (var roundTripped in new[] {
            JsonSerializer.Deserialize(JsonSerializer.Serialize(schema)),
            XmlSerializer.Deserialize(XmlSerializer.Serialize(schema)) })
        {
            Assert.That(roundTripped.NodeDefs.Single(d => d.Name == "FlagsDef").Kind, Is.EqualTo(SchemaNodeKind.PackedGroup));
            var builder = new SchemaSetBuilder();
            builder.AddDocument(roundTripped);
            var compiled = builder.Compile();
            var path = root ? "Doc.Flags" : "Doc.Root.Flags";
            var packed = compiled.FindByPath<SchemaPackedGroup>(path);
            Assert.That(packed.ByteLength is int length && length == 1, Is.True);
            Assert.That(packed.Members.Select(m => m.Name), Is.EqualTo(new[] { "Mode", "Channel" }));
            Assert.That(packed.Processors.Single().Key!.Value.ToString(), Is.EqualTo("test.packing"));
            Assert.That(packed.Processors.Single().EffectiveProperties.Single().Name, Is.EqualTo("test.packing.order"));
            Assert.That(packed.Processors.Single().EffectiveProperties.Single().Value, Is.EqualTo("little"));
            var mode = compiled.FindByPath<SchemaField>(path + ".Mode");
            Assert.That(mode.DataType!.Value.FullName, Is.EqualTo("Doc.Scaled"));
            Assert.That(mode.Processors.Single().Processor.FullName, Is.EqualTo("sys.scale"));
            Assert.That(roundTripped.Groups.Single(g => g.Name == "Flags"), Is.SameAs(packed));
            Assert.That(roundTripped.Fields.Single(f => f.Name == "Mode"), Is.SameAs(mode));
            Assert.That(roundTripped.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Mode", "Channel" }));
        }
    }

    [TestCase("group")]
    [TestCase("repeat")]
    [TestCase("choice")]
    public void Compile_PackedGroupInsideContainer_IsPreserved(string kind)
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson($$"""
            { "name": "Doc", "members": [ { "name": "Root", "kind": "{{kind}}", "members": [
              { "name": "Flags", "kind": "packedGroup", "byteLength": 1,
                "members": [ { "name": "Mode", "kind": "field", "datatype": "sys.uint8" } ] }
            ] } ] }
            """);
        Assert.That(builder.Compile().FindByPath("Doc.Root.Flags"), Is.TypeOf<SchemaPackedGroup>());
    }

    [Test]
    public void Compile_MissingCarrierLength_DefaultsToOneByte()
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson("""
            { "name": "Doc", "members": [ { "name": "Flags", "kind": "packedGroup", "members": [] } ] }
            """);

        var packed = (SchemaPackedGroup)builder.Compile().FindByPath("Doc.Flags");

        Assert.That(packed.ByteLength is int, Is.False);
        Assert.That(packed.EffectiveByteLength is int length && length == SchemaPackedGroup.DefaultByteLength && length == 1, Is.True);
    }

    [TestCase(", \"byteLength\": 0", "must be positive")]
    [TestCase(", \"byteLength\": -1", "must be positive")]
    public void Compile_InvalidCarrierLength_Fails(string length, string message)
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson($$"""
            { "name": "Doc", "members": [ { "name": "Flags", "kind": "packedGroup"{{length}}, "members": [] } ] }
            """);
        Assert.That(() => builder.Compile(), Throws.InvalidOperationException.With.Message.Contains(message));
    }

    [TestCase("group")]
    [TestCase("packedGroup")]
    [TestCase("repeat")]
    [TestCase("choice")]
    public void Compile_StructuralChild_Fails(string kind)
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson($$"""
            { "name": "Doc", "members": [ { "name": "Flags", "kind": "packedGroup", "byteLength": 1,
              "members": [ { "name": "Nested", "kind": "{{kind}}", "byteLength": 1, "members": [] } ] } ] }
            """);
        Assert.That(() => builder.Compile(), Throws.InvalidOperationException.With.Message.Contains("can only contain fields"));
    }

    [TestCase("\"byteLength\": 1")]
    [TestCase("\"byteLength\": { \"ref\": \"Flags.Length\" }")]
    [TestCase("\"byteLength\": { \"pub\": \"header.length\" }")]
    [TestCase("\"byteOffset\": 0")]
    public void Compile_ChildPhysicalAttributeFromNodeDef_Fails(string attribute)
    {
        var builder = new SchemaSetBuilder();
        builder.LoadFromJson($$"""
            { "name": "Doc", "nodeDefs": [ { "name": "Fields", "kind": "packedGroup", "members": [
                { "name": "Mode", "kind": "field", "datatype": "sys.uint8", {{attribute}} }
              ] } ],
              "members": [ { "name": "Root", "kind": "group", "members": [
                { "name": "Flags", "kind": "packedGroup", "byteLength": 1, "nodeDef": "Fields" }
              ] } ] }
            """);
        Assert.That(() => builder.Compile(), Throws.InvalidOperationException.With.Message.Contains("cannot declare byteLength or byteOffset"));
    }

    [Test]
    public void Compile_NodeDefKindMismatch_Fails()
    {
        var builder = new SchemaSetBuilder();
        builder.AddDocument(new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions => definitions.Group("Plain"))
            .Group("Root", g => g.PackedGroup("Flags", 1, nodeDef: "Plain"))
            .Build());
        Assert.That(() => builder.Compile(), Throws.InvalidOperationException.With.Message.Contains("kinds must match"));
    }

    [Test]
    public void Compile_PackedNodeDefWithInvalidMembers_Fails()
    {
        var builder = new SchemaSetBuilder();
        builder.AddDocument(new SchemaDocumentBuilder("Doc")
            .NodeDefs(definitions => definitions.PackedGroup("Flags", members: group => group.Group("Invalid")))
            .Group("Root")
            .Build());
        Assert.That(() => builder.Compile(), Throws.InvalidOperationException.With.Message.Contains("can only contain fields"));
    }

    [TestCase("byteLength=\"1\"", "")]
    [TestCase("", "<byteLength ref=\"Root.Length\" />")]
    [TestCase("", "<byteLength pub=\"header.length\" />")]
    public void Xsd_PackedGroupsAndNodeDefs_AreAccepted(string attribute, string element)
    {
        var xml = $"""
            <schema name="Doc">
              <nodeDefs><packedGroup name="FlagsDef"><members>
                <field name="Mode" datatype="sys.uint8" />
              </members></packedGroup></nodeDefs>
              <members>
                <packedGroup name="Flags" nodeDef="FlagsDef" {attribute}>{element}</packedGroup>
                <group name="Root"><members>
                  <field name="Length" datatype="sys.uint8" />
                  <repeat name="Items" count="2"><members>
                    <packedGroup name="Flags" byteLength="1"><members>
                      <field name="Mode" datatype="sys.uint8" />
                    </members></packedGroup>
                  </members></repeat>
                </members></group>
              </members>
            </schema>
            """;
        Assert.That(() => ValidateXml(xml), Throws.Nothing);
    }

    [TestCase("byteLength=\"0\"", "<field name=\"Mode\" datatype=\"sys.uint8\" />")]
    [TestCase("byteLength=\"-1\"", "<field name=\"Mode\" datatype=\"sys.uint8\" />")]
    [TestCase("byteLength=\"1\"", "<group name=\"Nested\" />")]
    public void Xsd_InvalidPackedGroups_AreRejected(string attribute, string member)
    {
        var xml = $"""
            <schema name="Doc"><members>
              <packedGroup name="Flags" {attribute}><members>{member}</members></packedGroup>
            </members></schema>
            """;
        Assert.That(() => ValidateXml(xml), Throws.TypeOf<System.Xml.Schema.XmlSchemaValidationException>());
    }

    private static void ValidateXml(string xml)
    {
        var settings = new System.Xml.XmlReaderSettings { ValidationType = System.Xml.ValidationType.Schema };
        settings.Schemas.Add(null, Path.Combine(TestContext.CurrentContext.TestDirectory, "schema.xsd"));
        using var input = new StringReader(xml);
        using var reader = System.Xml.XmlReader.Create(input, settings);
        while (reader.Read()) { }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Plan_PackedGroupWithoutPacker_IsRejected(bool root)
    {
        var document = new SchemaDocumentBuilder("Doc");
        if (root)
        {
            document.PackedGroup("Flags", 1, g => g.Field("Mode"));
        }
        else
        {
            document.Group("Root", g => g.PackedGroup("Flags", 1, f => f.Field("Mode")));
        }
        var builder = new SchemaSetBuilder();
        builder.AddDocument(document.Build());
        var compiled = builder.Compile();
        Assert.That(() => ExecutionPlan.Create(compiled, new SchemaName(root ? "Doc.Flags" : "Doc.Root"), new ProcessorManager()),
            Throws.TypeOf<ExecutionPlanException>().With.Message.Contains("must declare exactly one packing processor"));
    }
}
