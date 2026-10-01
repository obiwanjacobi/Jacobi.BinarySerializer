using Jacobi.BinarySerializer.Schema;

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
                  <pipeline>
                    <codec codec="root" />
                  </pipeline>
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
                  <pipeline>
                    <codec codec="root" />
                  </pipeline>
                  <children>
                    <field name="FieldA" type="Int32" scale="10">
                      <codec codec="identity" />
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
    public void Deserialize_TypeDefsAndCodecDefs_ArePreservedWithoutResolution()
    {
        var xml = """
            <schema name="TestSchema">
              <codecDefs>
                <codec codec="rootCodec" />
                <codec codec="deltaCodec">
                  <properties>
                    <property name="bits" value="7" />
                  </properties>
                </codec>
              </codecDefs>
              <typeDefs>
                <field name="CommonField" type="Int32">
                  <codec codec="deltaCodec" />
                  <properties>
                    <property name="scale" value="100" />
                  </properties>
                </field>
                <group name="CommonGroup">
                  <pipeline>
                    <codec codec="rootCodec" />
                  </pipeline>
                  <children>
                    <field name="InnerField" type="Int16">
                      <codec codec="deltaCodec" />
                    </field>
                  </children>
                </group>
              </typeDefs>
              <children>
                <group name="RootGroup">
                  <pipeline>
                    <codec codec="rootCodec" />
                  </pipeline>
                  <children>
                    <field name="Value" type="Int32">
                      <codec codec="deltaCodec" />
                    </field>
                  </children>
                </group>
              </children>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);

        Assert.That(document.CodecDefs.Count, Is.EqualTo(2));
        Assert.That(document.CodecDefs.Any(c => c.Codec == "rootCodec"));
        Assert.That(document.CodecDefs.Any(c => c.Codec == "deltaCodec" && c.Properties.Any(p => p.Name == "bits" && p.Value == "7")));

        Assert.That(document.TypeDefs.Count, Is.EqualTo(2));
        Assert.That(document.TypeDefs.OfType<SchemaField>().Any(f => f.Name == "CommonField" && f.Codec.Codec == "deltaCodec"));
        Assert.That(document.TypeDefs.OfType<SchemaGroup>().Any(g => g.Name == "CommonGroup" && g.Pipeline.Any(p => p.Codec == "rootCodec")));

        var rootGroup = document.Roots.Single();
        var rootField = rootGroup.Children.OfType<SchemaField>().Single();
        Assert.That(rootGroup.Pipeline.Single().Codec, Is.EqualTo("rootCodec"));
        Assert.That(rootField.Codec.Codec, Is.EqualTo("deltaCodec"));
    }
}
