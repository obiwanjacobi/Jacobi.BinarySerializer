using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Json;
using Jacobi.BinarySerializer.Schema.Xml;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>Repeat counts / choice indexes that refer to a public value or a field by schema path.</summary>
public class ValueReferenceTests
{
    private static SchemaGroup Schema(SchemaValueOrRef<int> count)
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = count };
        repeat.MemberList.Add(Field("Item"));
        return Group("Root", [], Field("Length"), repeat);
    }

    [Test]
    public void Build_PathReference_FlagsTargetFieldAndBindsKey()
    {
        var plan = Build(Schema(new SchemaNodeRef { Path = "Root.Length" }));

        var length = (FieldInfo)plan.Root.Members[0];
        var repeat = (RepeatInfo)plan.Root.Members[1];
        Assert.That(length.PublishesValue, Is.True);
        Assert.That(repeat.Count.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Length")));
    }

    [Test]
    public void Build_PublishedReference_BindsNamespaceAndName()
    {
        var plan = Build(Schema(new SchemaPubRef { Namespace = "hdr", Name = "count" }));

        var length = (FieldInfo)plan.Root.Members[0];
        var repeat = (RepeatInfo)plan.Root.Members[1];
        Assert.That(length.PublishesValue, Is.False);
        Assert.That(repeat.Count.Value, Is.EqualTo(new PublishedValueKey("hdr", "count")));
    }

    [Test]
    public void Build_PathReferenceToTargetAfterTheReferrer_IsAllowed()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = new SchemaNodeRef { Path = "Root.Length" } };
        repeat.MemberList.Add(Field("Item"));
        var root = Group("Root", [], repeat, Field("Length"));

        var plan = Build(root);

        Assert.That(((FieldInfo)plan.Root.Members[1]).PublishesValue, Is.True);
    }

    [Test]
    public void Build_PathReferenceToUnknownNode_ThrowsWithPaths()
    {
        var ex = Assert.Throws<ExecutionPlanException>(
            () => Build(Schema(new SchemaNodeRef { Path = "Root.Nope" })));

        Assert.That(ex!.Errors, Has.Count.EqualTo(1));
        Assert.That(ex.Errors[0], Does.Contain("Root.Items").And.Contain("Root.Nope"));
    }

    [Test]
    public void Build_PathReferenceToGroup_Throws()
    {
        var ex = Assert.Throws<ExecutionPlanException>(
            () => Build(Schema(new SchemaNodeRef { Path = "Root.Items" })));

        Assert.That(ex!.Errors[0], Does.Contain("must refer to a field"));
    }

    [Test]
    public void Build_ChoiceIndexPathReference_FlagsTargetField()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = new SchemaNodeRef { Path = "Root.Kind" } };
        choice.MemberList.Add(Field("A"));
        choice.MemberList.Add(Field("B"));

        var plan = Build(Group("Root", [], Field("Kind"), choice));

        Assert.That(((FieldInfo)plan.Root.Members[0]).PublishesValue, Is.True);
        Assert.That(((ChoiceInfo)plan.Root.Members[1]).SelectedIndex.Value,
            Is.EqualTo(PublishedValueKey.ForPath("Root.Kind")));
    }

    [Test]
    public void Xml_ValueReference_RoundTrips()
    {
        var xml = """
            <schema name="RefSchema">
              <members>
                <group name="Root">
                  <members>
                    <field name="Length" type="Int32" />
                    <repeat name="Items">
                      <count ref="Root.Length" />
                      <members>
                        <field name="Item" type="Int32" />
                      </members>
                    </repeat>
                  </members>
                </group>
              </members>
              <properties />
            </schema>
            """;

        var document = new SchemaSet().LoadFromXml(xml);
        var serialized = XmlSerializer.Serialize(document);
        var root = new SchemaSet().LoadFromXml(serialized).Roots.OfType<SchemaGroup>().Single();
        var repeat = root.Members.OfType<SchemaRepeat>().Single();

        Assert.That(serialized, Does.Contain("ref=\"Root.Length\""));
        Assert.That(repeat.Count is SchemaNodeRef r && r.Path == "Root.Length");
    }

    [Test]
    public void Json_ValueReference_RoundTrips()
    {
        var json = """
            {
              "name": "RefSchema",
              "members": [
                {
                  "kind": "repeat",
                  "name": "Items",
                  "count": { "pub": "hdr.count" },
                  "members": [
                    { "kind": "field", "name": "Item", "type": "Int32" }
                  ]
                }
              ]
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var serialized = JsonSerializer.Serialize(document);
        var repeat = JsonSerializer.Deserialize(serialized).Roots.OfType<SchemaRepeat>().Single();

        Assert.That(repeat.Count is SchemaPubRef r && r.Namespace == "hdr" && r.Name == "count");
    }
}
