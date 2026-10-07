using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Xml;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class XmlSerializerTests
{
    [Test]
    public void Deserialize_MinimalSchema_AreDeserialized()
    {
        var xml = """
            <schema name="TestSchema">
              <members />
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);

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
                    <property name="endianness" type="String" value="little" />
                  </properties>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var group = document.Groups.Single();

        Assert.That(group.Properties.Any(p => p.Name == "endianness" && p.Value == "little"));
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
                    <field name="FieldA" type="Int32" scale="10">
                      <processor processor="identity" />
                    </field>
                  </members>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
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
    public void Deserialize_TypeDefsAndProcessorDefs_ArePreservedWithoutResolution()
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
              <typeDefs>
                <field name="CommonField" type="Int32">
                    <processor processor="ref:deltaProcessor" />
                  <properties>
                    <property name="scale" value="100" />
                  </properties>
                </field>
                <group name="CommonGroup">
                  <processors>
                    <processor processor="ref:rootProcessor" />
                  </processors>
                  <members>
                    <field name="InnerField" type="Int16">
                      <processor processor="ref:deltaProcessor" />
                    </field>
                  </members>
                </group>
              </typeDefs>
              <members>
                <group name="RootGroup">
                  <processors>
                    <processor processor="ref:rootProcessor" />
                  </processors>
                  <members>
                    <field name="Value" typeDef="CommonField" type="Int32">
                      <processor processor="ref:deltaProcessor" />
                    </field>
                  </members>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);

        Assert.That(document.ProcessorDefs.Count, Is.EqualTo(2));
        Assert.That(document.ProcessorDefs.Any(p => p.Name == "rootProcessor" && p.Processor.ToString() == "sys.align"));
        Assert.That(document.ProcessorDefs.Any(p => p.Name == "deltaProcessor" && p.Processor.ToString() == "sys.scale" && p.Properties.Any(prop => prop.Name == "bits" && prop.Value == "7")));

        Assert.That(document.TypeDefs.Count, Is.EqualTo(2));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonField" && t.Processors.Any(p => p.Processor.IsReference && p.Processor.FullName == "deltaProcessor") && t.DataType == SchemaDataType.Int32));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonGroup" && t.Processors.Any(p => p.Processor.IsReference && p.Processor.FullName == "rootProcessor") && t.DataType == SchemaDataType.None));

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
                    <field name="Value" type="Int32">
                      <processor processor="identity" />
                    </field>
                  </members>
                </repeat>
                <choice name="ChoiceGroup" selectedIndex="2">
                  <processors>
                    <processor processor="ref:selectorProcessor" />
                  </processors>
                  <members>
                    <field name="OptionA" type="Int16" />
                    <field name="OptionB" type="Int16" />
                  </members>
                </choice>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var serialized = XmlSerializer.Serialize(document);

        Assert.That(serialized, Does.Contain("processorDefs"));
        Assert.That(serialized, Does.Contain("<processors>"));
        Assert.That(serialized, Does.Contain("pub=\"hdr.count\""));
        Assert.That(serialized, Does.Contain("selectedIndex=\"2\""));
        Assert.That(serialized, Does.Not.Contain("codec"));
        Assert.That(serialized, Does.Not.Contain("pipeline"));

        var roundTripped = new SchemaSet().LoadFromXml(serialized);
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

        var document = new SchemaSet().LoadFromXml(xml);
        var roundTripped = new SchemaSet().LoadFromXml(XmlSerializer.Serialize(document));

        foreach (var properties in new[] { document.Groups.Single().Properties, roundTripped.Groups.Single().Properties })
        {
            Assert.That(properties.Single(p => p.Name == "none").Value, Is.Null);
            Assert.That(properties.Single(p => p.Name == "empty").Value, Is.EqualTo(""));
        }
    }

    [Test]
    public void Serialize_RoundTrip_PreservesFieldValues()
    {
        var xml = """
            <schema name="ValueSchema">
              <members><group name="Root"><members>
                <field name="Constant" type="UInt32" value="0x89504E47" />
                <field name="Published" type="Int32"><value pub="hdr.magic" /></field>
                <field name="Node" type="Int32"><value ref="Constant" /></field>
                <field name="None" type="Int32" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var roundTripped = new SchemaSet().LoadFromXml(XmlSerializer.Serialize(document));
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

        var document = new SchemaSet().LoadFromXml(xml);
        var roundTripped = new SchemaSet().LoadFromXml(XmlSerializer.Serialize(document));
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
                <repeat name="Items"><size ref="Root.Len" /></repeat>
                <group name="Fixed" size="4"><members /></group>
                <choice name="Pick" selectedIndex="0" size="8" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var roundTripped = new SchemaSet().LoadFromXml(XmlSerializer.Serialize(document));
        var members = roundTripped.Groups.Single(g => g.Name == "Root").Members.OfType<SchemaGroup>().ToList();

        Assert.That(members[0], Is.InstanceOf<SchemaRepeat>());
        Assert.That(((SchemaRepeat)members[0]).Count is not (int or SchemaNodeRef or SchemaPubRef), Is.True);
        Assert.That(members[0].Size is SchemaNodeRef { Path: "Root.Len" }, Is.True);
        Assert.That(members[1].Size is 4, Is.True);
        Assert.That(members[2].Size is 8, Is.True);
    }

    [Test]
    public void Serialize_RoundTrip_PreservesFieldLength()
    {
        var xml = """
            <schema name="LengthSchema">
              <members><group name="Root"><members>
                <field name="Len" type="UInt8" />
                <field name="Blob" type="Bytes"><length ref="Root.Len" /></field>
                <field name="Sig" type="Bytes" length="2" value="0x8950" />
                <field name="Rest" type="Bytes" />
              </members></group></members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var roundTripped = new SchemaSet().LoadFromXml(XmlSerializer.Serialize(document));
        var fields = roundTripped.Groups.Single(g => g.Name == "Root").Members.OfType<SchemaField>().ToList();

        Assert.That(fields[1].DataType, Is.EqualTo(SchemaDataType.Bytes));
        Assert.That(fields[1].Length is SchemaNodeRef { Path: "Root.Len" }, Is.True);
        Assert.That(fields[2].Length is 2, Is.True);
        Assert.That(fields[2].Value is "0x8950", Is.True);
        Assert.That(fields[3].Length is not (int or SchemaNodeRef or SchemaPubRef), Is.True);
    }
}
