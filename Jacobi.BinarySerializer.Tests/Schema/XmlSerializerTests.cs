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
              <children />
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
              <children>
                <group name="RootGroup">
                  <processors>
                    <processor processor="root" />
                  </processors>
                  <children />
                  <properties>
                    <property name="endianness" type="String" value="little" />
                  </properties>
                </group>
              </children>
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
              <children>
                <group name="RootGroup" groupExtra="abc">
                  <processors>
                    <processor processor="root" />
                  </processors>
                  <children>
                    <field name="FieldA" type="Int32" scale="10">
                      <processor processor="identity" />
                    </field>
                  </children>
                </group>
              </children>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var group = document.Groups.Single();
        var field = (SchemaField)group.Children.Single();

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
                <processor processor="rootProcessor" />
                <processor processor="deltaProcessor">
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
                  <children>
                    <field name="InnerField" type="Int16">
                      <processor processor="ref:deltaProcessor" />
                    </field>
                  </children>
                </group>
              </typeDefs>
              <children>
                <group name="RootGroup">
                  <processors>
                    <processor processor="ref:rootProcessor" />
                  </processors>
                  <children>
                    <field name="Value" typeDef="CommonField" type="Int32">
                      <processor processor="ref:deltaProcessor" />
                    </field>
                  </children>
                </group>
              </children>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);

        Assert.That(document.ProcessorDefs.Count, Is.EqualTo(2));
        Assert.That(document.ProcessorDefs.Any(p => p.Processor.FullName == "rootProcessor"));
        Assert.That(document.ProcessorDefs.Any(p => p.Processor.FullName == "deltaProcessor" && p.Properties.Any(prop => prop.Name == "bits" && prop.Value == "7")));

        Assert.That(document.TypeDefs.Count, Is.EqualTo(2));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonField" && t.Processors.Any(p => p.Processor.FullName == "ref:deltaProcessor") && t.Type == SchemaDataType.Int32));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonGroup" && t.Processors.Any(p => p.Processor.FullName == "ref:rootProcessor") && t.Type == SchemaDataType.None));

        var rootGroup = document.Roots.Single();
        var rootField = rootGroup.Children.OfType<SchemaField>().Single();
        Assert.That(rootGroup.Processors.Single().Processor.FullName, Is.EqualTo("ref:rootProcessor"));
        Assert.That(rootField.Processors.Single().Processor.FullName, Is.EqualTo("ref:deltaProcessor"));
    }

    [Test]
    public void Serialize_RoundTrip_UsesProcessorContractNames_AndPreservesRepeatChoiceValues()
    {
        var xml = """
            <schema name="RoundTripSchema">
              <processorDefs>
                <processor processor="counterProcessor" />
                <processor processor="selectorProcessor" />
              </processorDefs>
              <children>
                <repeat name="RepeatGroup">
                  <count ref="hdr/count" />
                  <processors>
                    <processor processor="rootProcessor" />
                  </processors>
                  <children>
                    <field name="Value" type="Int32">
                      <processor processor="identity" />
                    </field>
                  </children>
                </repeat>
                <choice name="ChoiceGroup" selectedIndex="2">
                  <processors>
                    <processor processor="ref:selectorProcessor" />
                  </processors>
                  <children>
                    <field name="OptionA" type="Int16" />
                    <field name="OptionB" type="Int16" />
                  </children>
                </choice>
              </children>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var serialized = XmlSerializer.Serialize(document);

        Assert.That(serialized, Does.Contain("processorDefs"));
        Assert.That(serialized, Does.Contain("<processors>"));
        Assert.That(serialized, Does.Contain("ref=\"hdr/count\""));
        Assert.That(serialized, Does.Contain("selectedIndex=\"2\""));
        Assert.That(serialized, Does.Not.Contain("codec"));
        Assert.That(serialized, Does.Not.Contain("pipeline"));

        var roundTripped = new SchemaSet().LoadFromXml(serialized);
        var repeat = roundTripped.Roots.OfType<SchemaRepeat>().Single();
        var choice = roundTripped.Roots.OfType<SchemaChoice>().Single();

        Assert.That(repeat.Count is SchemaValueRef repeatCount && repeatCount.Reference == "hdr/count");
        Assert.That(choice.SelectedIndex is int selected && selected == 2);
    }
}
