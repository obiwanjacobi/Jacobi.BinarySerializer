using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Schema.Json;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class JsonSerializerTests
{
    [Test]
    public void Deserialize_MinimalSchema_AreDeserialized()
    {
        var json = @"
        {
            ""Name"": ""TestSchema"",
            ""members"": [
            ],
            ""Properties"": [
            ]
        }";

        var document = JsonSerializer.Deserialize(json);
        Assert.AreEqual("TestSchema", document.Name);
    }

    [Test]
    public void Deserialize_ExplicitProperties_ArePreserved()
    {
        var json = @"
        {
            ""name"": ""TestSchema"",
            ""members"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""root"" }
                    ],
                    ""members"": [],
                    ""properties"": [
                        { ""name"": ""endianness"", ""type"": ""String"", ""value"": ""little"" }
                    ]
                }
            ],
            ""properties"": []
        }";

        var document = JsonSerializer.Deserialize(json);
        var group = document.Groups.Single();

        Assert.That(group.Properties.Any(p => p.Name == "endianness" && p.Value == "little"));
    }

    [Test]
    public void Deserialize_UnknownNodeFields_AreAddedToProperties()
    {
        var json = @"
        {
            ""name"": ""TestSchema"",
            ""members"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""root"" }
                    ],
                    ""members"": [
                        {
                            ""name"": ""FieldA"",
                            ""kind"": ""field"",
                            ""processors"": [
                                { ""processor"": ""identity"" }
                            ],
                            ""type"": ""Int32"",
                            ""scale"": 10
                        }
                    ],
                    ""groupExtra"": ""abc""
                }
            ],
            ""properties"": []
        }";

        var document = JsonSerializer.Deserialize(json);
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
        var json = @"
        {
            ""name"": ""TestSchema"",
            ""processorDefs"": [
                {
                    ""name"": ""rootProcessor"", ""processor"": ""sys.align""
                },
                {
                    ""name"": ""deltaProcessor"", ""processor"": ""sys.scale"",
                    ""properties"": [
                        { ""name"": ""bits"", ""value"": ""7"" }
                    ]
                }
            ],
            ""typeDefs"": [
                {
                    ""name"": ""CommonField"",
                    ""kind"": ""field"",
                    ""processors"": [
                        { ""processor"": ""ref:deltaProcessor"" }
                    ],
                    ""type"": ""Int32"",
                    ""properties"": [
                        { ""name"": ""scale"", ""value"": ""100"" }
                    ]
                },
                {
                    ""name"": ""CommonGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""ref:rootProcessor"" }
                    ],
                    ""members"": [
                        {
                            ""name"": ""InnerField"",
                            ""kind"": ""field"",
                            ""processors"": [
                                { ""processor"": ""ref:deltaProcessor"" }
                            ],
                            ""type"": ""Int16""
                        }
                    ]
                }
            ],
            ""members"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""ref:rootProcessor"" }
                    ],
                    ""members"": [
                        {
                            ""name"": ""Value"",
                            ""typeDef"": ""CommonField"",
                            ""kind"": ""field"",
                            ""processors"": [
                                { ""processor"": ""ref:deltaProcessor"" }
                            ],
                            ""type"": ""Int32""
                        }
                    ]
                }
            ],
            ""properties"": []
        }";

        var document = JsonSerializer.Deserialize(json);

        Assert.That(document.ProcessorDefs.Count, Is.EqualTo(2));
        Assert.That(document.ProcessorDefs.Any(p => p.Name == "rootProcessor" && p.Processor.ToString() == "sys.align"));
        Assert.That(document.ProcessorDefs.Any(p => p.Name == "deltaProcessor" && p.Processor.ToString() == "sys.scale" && p.Properties.Any(prop => prop.Name == "bits" && prop.Value == "7")));

        Assert.That(document.TypeDefs.Count, Is.EqualTo(2));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonField" && t.Processors.Any(p => p.Processor.IsReference && p.Processor.FullName == "deltaProcessor") && t.DataType == "Int32"));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonGroup" && t.Processors.Any(p => p.Processor.IsReference && p.Processor.FullName == "rootProcessor") && t.DataType is null));

        // Resolver not implemented yet: usage remains as raw references.
        var rootGroup = document.Roots.Single();
        var rootField = rootGroup.Members.OfType<SchemaField>().Single();
        Assert.That(rootGroup.Processors.Single().Processor.ToString(), Is.EqualTo("ref:rootProcessor"));
        Assert.That(rootField.Processors.Single().Processor.ToString(), Is.EqualTo("ref:deltaProcessor"));
    }

    [Test]
    public void Serialize_RoundTrip_UsesProcessorContractNames_AndPreservesRepeatChoiceValues()
    {
        var json = """
            {
              "name": "RoundTripSchema",
              "processorDefs": [
                { "name": "counterProcessor", "processor": "sys.align" },
                { "name": "selectorProcessor", "processor": "sys.align" }
              ],
              "members": [
                {
                  "kind": "repeat",
                  "name": "RepeatGroup",
                  "count": { "pub": "hdr.count" },
                  "processors": [
                    { "processor": "rootProcessor" }
                  ],
                  "members": [
                    {
                      "kind": "field",
                      "name": "Value",
                      "type": "Int32",
                      "processors": [
                        { "processor": "identity" }
                      ]
                    }
                  ]
                },
                {
                  "kind": "choice",
                  "name": "ChoiceGroup",
                  "selectedIndex": 2,
                  "processors": [
                    { "processor": "ref:selectorProcessor" }
                  ],
                  "members": [
                    {
                      "kind": "field",
                      "name": "OptionA",
                      "type": "Int16"
                    },
                    {
                      "kind": "field",
                      "name": "OptionB",
                      "type": "Int16"
                    }
                  ]
                }
              ],
              "properties": []
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var serialized = JsonSerializer.Serialize(document);

        Assert.That(serialized, Does.Contain("\"ProcessorDefs\""));
        Assert.That(serialized, Does.Contain("\"Count\""));
        Assert.That(serialized, Does.Contain("\"SelectedIndex\""));
        Assert.That(serialized, Does.Not.Contain("codec"));
        Assert.That(serialized, Does.Not.Contain("pipeline"));

        var roundTripped = JsonSerializer.Deserialize(serialized);
        var repeat = roundTripped.Roots.OfType<SchemaRepeat>().Single();
        var choice = roundTripped.Roots.OfType<SchemaChoice>().Single();

        Assert.That(repeat.Count is SchemaPubRef repeatCount && repeatCount.Namespace == "hdr" && repeatCount.Name == "count");
        Assert.That(choice.SelectedIndex is int selected && selected == 2);
    }

    [Test]
    public void Serialize_RoundTrip_TypeDefWithoutDataType_StaysNull()
    {
        var json = """
            {
              "name": "TypeDefSchema",
              "typeDefs": [ { "name": "CommonGroup", "kind": "group", "processors": [] } ],
              "members": [ { "kind": "group", "name": "Root", "members": [] } ]
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(document));

        Assert.That(roundTripped.TypeDefs.Single().DataType, Is.Null);
    }

    [Test]
    public void Serialize_RoundTrip_PreservesFieldValues()
    {
        var json = """
            {
              "name": "ValueSchema",
              "members": [ { "kind": "group", "name": "Root", "members": [
                { "kind": "field", "name": "Constant", "type": "UInt32", "value": "0x89504E47" },
                { "kind": "field", "name": "Published", "type": "Int32", "value": { "pub": "hdr.magic" } },
                { "kind": "field", "name": "Node", "type": "Int32", "value": { "ref": "Constant" } },
                { "kind": "field", "name": "None", "type": "Int32" }
              ] } ],
              "properties": []
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(document));
        var fields = roundTripped.Groups.Single().Members.OfType<SchemaField>().ToDictionary(f => f.Name);

        Assert.That(fields["Constant"].Value is string text && text == "0x89504E47");
        Assert.That(fields["Published"].Value is SchemaPubRef pub && pub.Namespace == "hdr" && pub.Name == "magic");
        Assert.That(fields["Node"].Value is SchemaNodeRef);
        Assert.That(fields["None"].Value is string or SchemaPubRef or SchemaNodeRef, Is.False);
    }

    [Test]
    public void Serialize_RoundTrip_PreservesValueProcessors()
    {
        var json = """
            {
              "name": "VpSchema",
              "members": [ { "kind": "group", "name": "Root", "members": [
                { "kind": "repeat", "name": "Items", "count": { "ref": "Root.Kind" },
                  "valueProcessors": [ { "processor": "sys.map", "properties": [ { "name": "a", "value": "1" } ] } ], "members": [] },
                { "kind": "choice", "name": "Pick", "selectedIndex": { "ref": "Root.Kind" },
                  "valueProcessors": [ { "processor": "sys.map", "properties": [ { "name": "b", "value": "2" } ] } ], "members": [] }
              ] } ],
              "properties": []
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(document));
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
        var json = """
            {
              "name": "SizeSchema",
              "members": [ { "kind": "group", "name": "Root", "members": [
                { "kind": "repeat", "name": "Items", "byteSize": { "ref": "Root.Len" }, "members": [] },
                { "kind": "group", "name": "Fixed", "byteSize": 4, "members": [] },
                { "kind": "choice", "name": "Pick", "selectedIndex": 0, "byteSize": 8, "members": [] }
              ] } ],
              "properties": []
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(document));
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
        var json = """
            {
              "name": "LengthSchema",
              "members": [ { "kind": "group", "name": "Root", "members": [
                { "kind": "field", "name": "Len", "type": "UInt8" },
                { "kind": "field", "name": "Blob", "type": "Bytes", "byteLength": { "ref": "Root.Len" } },
                { "kind": "field", "name": "Sig", "type": "Bytes", "byteLength": 2, "value": "0x8950" },
                { "kind": "field", "name": "Rest", "type": "Bytes" }
              ] } ],
              "properties": []
            }
            """;

        var document = JsonSerializer.Deserialize(json);
        var roundTripped = JsonSerializer.Deserialize(JsonSerializer.Serialize(document));
        var fields = roundTripped.Groups.Single(g => g.Name == "Root").Members.OfType<SchemaField>().ToList();

        Assert.That(fields[1].DataType, Is.EqualTo(new SchemaDataType("Bytes")));
        Assert.That(fields[1].ByteLength is SchemaNodeRef { Path: "Root.Len" }, Is.True);
        Assert.That(fields[2].ByteLength is 2, Is.True);
        Assert.That(fields[2].Value is "0x8950", Is.True);
        Assert.That(fields[3].ByteLength is not (int or SchemaNodeRef or SchemaPubRef), Is.True);
    }
}
