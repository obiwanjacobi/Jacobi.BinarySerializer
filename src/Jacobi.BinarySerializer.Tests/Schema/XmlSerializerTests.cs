using System.Xml.Linq;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Xml;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class XmlSerializerTests
{
  [Test]
  public void Deserialize_LegacyNodeDef_FailsInsteadOfDiscardingDefinition()
  {
    const string xml = """
      <schema name="Doc"><nodeDefs><nodeDef name="Old" kind="field" datatype="sys.uint8" /></nodeDefs></schema>
      """;
    Assert.That(() => XmlSerializer.Deserialize(xml), Throws.InvalidOperationException.With.Message.Contains("typed field"));
  }

  [Test]
  public void Serialize_OmitsUnusedNamespacesAndEmptyCollections()
  {
    var schema = new SchemaDocumentBuilder("Test").Group("Root", group => group.Field("Value")).Build();
    var text = XmlSerializer.Serialize(schema);
    Assert.That(text, Does.Not.Contain("xmlns:xsi").And.Not.Contain("xmlns:xsd"));
    var xml = XDocument.Parse(text);
    Assert.That(xml.Descendants().Where(element => !element.HasAttributes && !element.HasElements), Is.Empty);
    var restored = XmlSerializer.Deserialize(text);
    Assert.That(restored.Fields.Single().Name, Is.EqualTo("Value"));
  }

    [Test]
    public void RoundTrip_AbsentAndEmptyCollectionsAreEquivalent()
    {
        const string source = """
            <schema name="Test">
              <includes />
              <properties />
              <nodeDefs><group name="Body"><processors /><members /><properties /></group></nodeDefs>
              <dataTypeDefs><dataTypeDef name="Scaled" basedOn="sys.int16"><options /><processors /><properties /></dataTypeDef></dataTypeDefs>
              <processorDefs><processor name="Scale" processor="sys.scale"><properties /></processor></processorDefs>
              <members><group name="Root">
                <properties />
                <processors><processor name="sys.align"><properties /></processor></processors>
                <members>
                  <field name="Value" datatype="sys.uint8"><properties /><processor name="sys.scale"><properties /></processor></field>
                  <group name="Flags"><members /><processors /><properties /></group>
                  <repeat name="Items"><members /><processors /><properties /><valueProcessors /></repeat>
                  <choice name="Variant"><members /><processors /><properties /><valueProcessors /></choice>
                </members>
              </group></members>
            </schema>
            """;
        var withoutEmpty = XDocument.Parse(source);
        withoutEmpty.Descendants().Where(element => !element.HasAttributes && !element.HasElements
            && String.IsNullOrWhiteSpace(element.Value)).Remove();
        var fromEmpty = XmlSerializer.Deserialize(source);
        var fromAbsent = XmlSerializer.Deserialize(withoutEmpty.ToString());

        Assert.That(XmlSerializer.Serialize(fromEmpty), Is.EqualTo(XmlSerializer.Serialize(fromAbsent)));
        foreach (var schema in new[] { fromEmpty, fromAbsent })
        {
            Assert.That(schema.Includes, Is.Empty);
            Assert.That(schema.Properties, Is.Empty);
            Assert.That(((SchemaGroup)schema.NodeDefs.Single()).Members, Is.Empty);
            Assert.That(((SchemaGroup)schema.NodeDefs.Single()).Processors, Is.Empty);
            Assert.That(schema.DataTypeDefs.Single().Options, Is.Null);
            Assert.That(schema.DataTypeDefs.Single().Processors, Is.Empty);
            Assert.That(schema.ProcessorDefs.Single().Properties, Is.Empty);
            var root = schema.Roots.Single();
            Assert.That(root.Processors.Single().Properties, Is.Empty);
            Assert.That(schema.Fields.Single().Processors.Single().Properties, Is.Empty);
            foreach (var group in root.Members.OfType<SchemaGroup>())
            {
                Assert.That(group.Members, Is.Empty);
                Assert.That(group.Processors, Is.Empty);
                Assert.That(group.Properties, Is.Empty);
            }
            Assert.That(root.Members.OfType<SchemaRepeat>().Single().ValueProcessors, Is.Empty);
            Assert.That(root.Members.OfType<SchemaChoice>().Single().ValueProcessors, Is.Empty);
        }
        var dto = XmlSchemaMapper.FromSchema(fromEmpty);
        Assert.That(dto.Includes, Is.Null);
        Assert.That(dto.Properties, Is.Null);
        var nodeDefinition = (XmlSchemaGroup)dto.NodeDefs!.Single();
        Assert.That(nodeDefinition.Members, Is.Null);
        Assert.That(nodeDefinition.Processors, Is.Null);
        Assert.That(dto.DataTypeDefs!.Single().Options, Is.Null);
        Assert.That(dto.ProcessorDefs!.Single().Properties, Is.Null);
    }

    [Test]
    public void RoundTrip_PreservesInlineNodeDefAndWrappedFieldProcessors()
    {
        var document = XmlSerializer.Deserialize("""
            <schema name="Roland">
              <nodeDefs>
                <field name="sysex16" datatype="sys.uint16" byteLength="2">
                  <processor name="ref:midi.sysexbyte" />
                </field>
              </nodeDefs>
              <members><group name="Message"><members>
                <field name="Address" nodeDef="sysex16">
                  <processors><processor name="sys.scale" scale="2" /></processors>
                </field>
              </members></group></members>
            </schema>
            """);
        var restored = XmlSerializer.Deserialize(XmlSerializer.Serialize(document));
        foreach (var schema in new[] { document, restored })
        {
            var definition = (SchemaField)schema.NodeDefs.Single();
            Assert.That(definition.Processors.Single().Processor.ToString(), Is.EqualTo("ref:midi.sysexbyte"));
            Assert.That(definition.Properties.Any(property => property.Name == "processor"), Is.False);
            Assert.That(definition.ByteLength is int byteLength && byteLength == 2, Is.True);
            Assert.That(schema.Fields.Single().Processors.Single().Processor.ToString(), Is.EqualTo("sys.scale"));
        }
    }

    [Test]
    public void Deserialize_MinimalSchema_AreDeserialized()
    {
        var xml = """
            <schema name="TestSchema">
              <members />
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);

        Assert.AreEqual("TestSchema", document.Name);
    }

    [Test]
    public void Deserialize_ExplicitProperties_ArePreserved()
    {
        var xml = """
            <schema name="TestSchema">
              <members>
                <group name="RootGroup">
                  <processors>
                    <processor processor="root" />
                  </processors>
                  <members />
                  <properties>
                    <property name="endianness" type="sys.string" value="little" />
                  </properties>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var group = document.Groups.Single();

        Assert.That(group.Properties.Any(p => p.Name == "endianness" && p.Value == "little"));
    }

    [Test]
    public void Deserialize_ProcessorPubNs_IsNotAProcessorProperty()
    {
        var xml = """
            <schema name="TestSchema">
              <members>
                <group name="RootGroup">
                  <processors>
                    <processor processor="root" pubns="hdr" extra="x" />
                  </processors>
                  <members />
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var processor = document.Groups.Single().Processors.Single();

        Assert.That(processor.PublishNamespace, Is.EqualTo("hdr"));
        Assert.That(processor.Properties.Select(p => p.Name), Is.EquivalentTo(new[] { "extra" }));
    }

    [Test]
    public void Deserialize_UnknownNodeFields_AreAddedToProperties()
    {
        var xml = """
            <schema name="TestSchema">
              <members>
                <group name="RootGroup" groupExtra="abc">
                  <processors>
                    <processor processor="root" />
                  </processors>
                  <members>
                    <field name="FieldA" datatype="sys.int32" scale="10">
                      <processor processor="identity" />
                    </field>
                  </members>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var group = document.Groups.Single();
        var field = (SchemaField)group.Members.Single();

        Assert.That(document.Roots.Count, Is.EqualTo(1));
        Assert.That(document.Roots.Single().Name, Is.EqualTo("RootGroup"));
        Assert.That(document.Groups.Count, Is.EqualTo(1));
        Assert.That(document.Fields.Count, Is.EqualTo(1));
        Assert.That(group.Properties.Any(p => p.Name == "groupExtra" && p.Value == "abc"));
        Assert.That(field.Properties.Any(p => p.Name == "scale" && p.Value == "10"));
    }

    [Test]
    public void Deserialize_NodeDefsAndProcessorDefs_ArePreservedWithoutResolution()
    {
        var xml = """
            <schema name="TestSchema">
              <processorDefs>
                <processor name="rootProcessor" processor="sys.align" />
                <processor name="deltaProcessor" processor="sys.scale">
                  <properties>
                    <property name="bits" value="7" />
                  </properties>
                </processor>
              </processorDefs>
              <nodeDefs>
                <field name="CommonField" datatype="sys.int32">
                  <processors>
                    <processor name="ref:deltaProcessor" />
                  </processors>
                  <properties>
                    <property name="scale" value="100" />
                  </properties>
                </field>
                <group name="CommonGroup">
                  <processors>
                    <processor name="ref:rootProcessor" />
                  </processors>
                </group>
              </nodeDefs>
              <members>
                <group name="RootGroup">
                  <processors>
                    <processor name="ref:rootProcessor" />
                  </processors>
                  <members>
                    <field name="Value" nodeDef="CommonField" datatype="sys.int32">
                      <processor name="ref:deltaProcessor" />
                    </field>
                  </members>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);

        Assert.That(document.ProcessorDefs.Count, Is.EqualTo(2));
        Assert.That(document.ProcessorDefs.Any(p => p.Name == "rootProcessor" && p.Processor.ToString() == "sys.align"));
        Assert.That(document.ProcessorDefs.Any(p => p.Name == "deltaProcessor" && p.Processor.ToString() == "sys.scale" && p.Properties.Any(prop => prop.Name == "bits" && prop.Value == "7")));

        Assert.That(document.NodeDefs.Count, Is.EqualTo(2));
        Assert.That(document.NodeDefs.OfType<SchemaField>().Any(node => node.Name == "CommonField" && node.Processors.Any(processor => processor.Processor.FullName == "deltaProcessor") && node.DataType == "sys.int32"));
        Assert.That(document.NodeDefs.OfType<SchemaGroup>().Any(node => node.Name == "CommonGroup" && node.Processors.Any(processor => processor.Processor.FullName == "rootProcessor")));

        var rootGroup = document.Roots.Single();
        var rootField = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(rootGroup.Processors.Single().Processor.ToString(), Is.EqualTo("ref:rootProcessor"));
        Assert.That(rootField.Processors.Single().Processor.ToString(), Is.EqualTo("ref:deltaProcessor"));
    }

    [Test]
    public void Serialize_RoundTrip_UsesProcessorContractNames_AndPreservesRepeatChoiceValues()
    {
        var xml = """
            <schema name="RoundTripSchema">
              <processorDefs>
                <processor name="counterProcessor" processor="sys.align" />
                <processor name="selectorProcessor" processor="sys.align" />
              </processorDefs>
              <members>
                <repeat name="RepeatGroup">
                  <count pub="hdr.count" />
                  <processors>
                    <processor processor="sys.align" />
                  </processors>
                  <members>
                    <field name="Value" datatype="sys.int32">
                      <processor processor="identity" />
                    </field>
                  </members>
                </repeat>
                <choice name="ChoiceGroup" selectedIndex="2">
                  <processors>
                    <processor processor="ref:selectorProcessor" />
                  </processors>
                  <members>
                    <field name="OptionA" datatype="sys.int16" />
                    <field name="OptionB" datatype="sys.int16" />
                  </members>
                </choice>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var serialized = XmlSerializer.Serialize(document);

        Assert.That(serialized, Does.Contain("processorDefs"));
        Assert.That(serialized, Does.Contain("<processors>"));
        Assert.That(serialized, Does.Contain("pub=\"hdr.count\""));
        Assert.That(serialized, Does.Contain("selectedIndex=\"2\""));
        Assert.That(serialized, Does.Not.Contain("codec"));
        Assert.That(serialized, Does.Not.Contain("pipeline"));

        var roundTripped = new SchemaSetBuilder().LoadFromXml(serialized);
        var repeat = roundTripped.Roots.OfType<SchemaRepeat>().Single();
        var choice = roundTripped.Roots.OfType<SchemaChoice>().Single();

        Assert.That(repeat.Count is SchemaPubRef repeatCount && repeatCount.Namespace == "hdr" && repeatCount.Name == "count");
        Assert.That(choice.SelectedIndex is int selected && selected == 2);
    }

    [Test]
    public void Serialize_RoundTrip_PreservesNilProperty()
    {
        var xml = """
            <schema name="NilSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <members>
                <group name="Root">
                  <members />
                  <properties>
                    <property name="empty" value="" />
                    <property name="none" xsi:nil="true" />
                  </properties>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var roundTripped = new SchemaSetBuilder().LoadFromXml(XmlSerializer.Serialize(document));

        foreach (var properties in new[] { document.Groups.Single().Properties, roundTripped.Groups.Single().Properties })
        {
            Assert.That(properties.Single(p => p.Name == "none").Value, Is.Null);
            Assert.That(properties.Single(p => p.Name == "empty").Value, Is.EqualTo(""));
        }
    }

    [Test]
    public void Serialize_RoundTrip_PreservesDataTypeDefs()
    {
        var xml = """
            <schema name="DefSchema">
              <dataTypeDefs>
                <dataTypeDef name="Celsius" basedOn="sys.int16" min="-40" max="125" scale="10">
                  <options><option value="1" name="One" /></options>
                  <processors><processor name="sys.scale" /></processors>
                </dataTypeDef>
              </dataTypeDefs>
              <members><group name="Root"><members>
                <field name="Temp" datatype="DefSchema.Celsius" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var roundTripped = new SchemaSetBuilder().LoadFromXml(XmlSerializer.Serialize(document));

        foreach (var def in new[] { document.DataTypeDefs.Single(), roundTripped.DataTypeDefs.Single() })
        {
            Assert.That(def.Name, Is.EqualTo("Celsius"));
            Assert.That(def.BasedOn.FullName, Is.EqualTo("sys.int16"));
            Assert.That(def.Min, Is.EqualTo(-40m));
            Assert.That(def.Max, Is.EqualTo(125m));
            Assert.That(def.Scale, Is.EqualTo(10m));
            Assert.That(def.Options![1], Is.EqualTo("One"));
            Assert.That(def.Processors, Has.Count.EqualTo(1));
        }
    }

    [Test]
    public void Serialize_RoundTrip_PreservesFieldValues()
    {
        var xml = """
            <schema name="ValueSchema">
              <members><group name="Root"><members>
                <field name="Constant" datatype="sys.uint32" value="0x89504E47" />
                <field name="Published" datatype="sys.int32"><value pub="hdr.magic" /></field>
                <field name="Node" datatype="sys.int32"><value ref="Constant" /></field>
                <field name="None" datatype="sys.int32" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var roundTripped = new SchemaSetBuilder().LoadFromXml(XmlSerializer.Serialize(document));
        var fields = roundTripped.Groups.Single().Members.OfType<SchemaField>().ToDictionary(f => f.Name);

        Assert.That(fields["Constant"].Value is string text && text == "0x89504E47");
        Assert.That(fields["Published"].Value is SchemaPubRef pub && pub.Namespace == "hdr" && pub.Name == "magic");
        Assert.That(fields["Node"].Value is SchemaNodeRef);
        Assert.That(fields["None"].Value is string or SchemaPubRef or SchemaNodeRef, Is.False);
    }

    [Test]
    public void Serialize_RoundTrip_PreservesValueProcessors()
    {
        var xml = """
            <schema name="VpSchema">
              <members><group name="Root"><members>
                <repeat name="Items"><count ref="Root.Kind" />
                  <valueProcessors><processor processor="sys.map"><properties><property name="a" value="1" /></properties></processor></valueProcessors>
                </repeat>
                <choice name="Pick"><selectedIndex ref="Root.Kind" />
                  <valueProcessors><processor processor="sys.map"><properties><property name="b" value="2" /></properties></processor></valueProcessors>
                </choice>
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var roundTripped = new SchemaSetBuilder().LoadFromXml(XmlSerializer.Serialize(document));
        var members = roundTripped.Groups.Single(g => g.Name == "Root").Members;
        var repeat = members.OfType<SchemaRepeat>().Single();
        var choice = members.OfType<SchemaChoice>().Single();

        Assert.That(repeat.ValueProcessors, Has.Count.EqualTo(1));
        Assert.That(repeat.ValueProcessors[0].Properties.Single().Value, Is.EqualTo("1"));
        Assert.That(choice.ValueProcessors, Has.Count.EqualTo(1));
        Assert.That(choice.ValueProcessors[0].Properties.Single().Value, Is.EqualTo("2"));
    }

    [Test]
    public void Serialize_RoundTrip_PreservesSizeAndAbsentCount()
    {
        var xml = """
            <schema name="SizeSchema">
              <members><group name="Root"><members>
                <repeat name="Items"><byteSize ref="Root.Len" /></repeat>
                <group name="Fixed" byteSize="4"><members /></group>
                <choice name="Pick" selectedIndex="0" byteSize="8" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var roundTripped = new SchemaSetBuilder().LoadFromXml(XmlSerializer.Serialize(document));
        var members = roundTripped.Groups.Single(g => g.Name == "Root").Members.OfType<SchemaGroup>().ToList();

        Assert.That(members[0], Is.InstanceOf<SchemaRepeat>());
        Assert.That(((SchemaRepeat)members[0]).Count is not (int or SchemaNodeRef or SchemaPubRef), Is.True);
        Assert.That(members[0].ByteSize is SchemaNodeRef { Path: "Root.Len" }, Is.True);
        Assert.That(members[1].ByteSize is 4, Is.True);
        Assert.That(members[2].ByteSize is 8, Is.True);
    }

    [Test]
    public void Serialize_RoundTrip_PreservesFieldLength()
    {
        var xml = """
            <schema name="LengthSchema">
              <members><group name="Root"><members>
                <field name="Len" datatype="sys.uint8" />
                <field name="Blob" datatype="sys.bytes"><byteLength ref="Root.Len" /></field>
                <field name="Sig" datatype="sys.bytes" byteLength="2" value="0x8950" />
                <field name="Rest" datatype="sys.bytes" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSetBuilder().LoadFromXml(xml);
        var roundTripped = new SchemaSetBuilder().LoadFromXml(XmlSerializer.Serialize(document));
        var fields = roundTripped.Groups.Single(g => g.Name == "Root").Members.OfType<SchemaField>().ToList();

        Assert.That(fields[1].DataType, Is.EqualTo(new SchemaName("sys.bytes")));
        Assert.That(fields[1].ByteLength is SchemaNodeRef { Path: "Root.Len" }, Is.True);
        Assert.That(fields[2].ByteLength is 2, Is.True);
        Assert.That(fields[2].Value is "0x8950", Is.True);
        Assert.That(fields[3].ByteLength is not (int or SchemaNodeRef or SchemaPubRef), Is.True);
    }
}

