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
            ""Children"": [
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
            ""children"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""root"" }
                    ],
                    ""children"": [],
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
            ""children"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""root"" }
                    ],
                    ""children"": [
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
        var json = @"
        {
            ""name"": ""TestSchema"",
            ""processorDefs"": [
                {
                    ""processor"": ""rootProcessor""
                },
                {
                    ""processor"": ""deltaProcessor"",
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
                    ""children"": [
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
            ""children"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""processors"": [
                        { ""processor"": ""ref:rootProcessor"" }
                    ],
                    ""children"": [
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
        Assert.That(document.ProcessorDefs.Any(p => p.Processor.FullName == "rootProcessor"));
        Assert.That(document.ProcessorDefs.Any(p => p.Processor.FullName == "deltaProcessor" && p.Properties.Any(prop => prop.Name == "bits" && prop.Value == "7")));

        Assert.That(document.TypeDefs.Count, Is.EqualTo(2));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonField" && t.Processors.Any(p => p.Processor.FullName == "ref:deltaProcessor") && t.Type == SchemaDataType.Int32));
        Assert.That(document.TypeDefs.Any(t => t.Name == "CommonGroup" && t.Processors.Any(p => p.Processor.FullName == "ref:rootProcessor") && t.Type == SchemaDataType.None));

        // Resolver not implemented yet: usage remains as raw references.
        var rootGroup = document.Roots.Single();
        var rootField = rootGroup.Children.OfType<SchemaField>().Single();
        Assert.That(rootGroup.Processors.Single().Processor.FullName, Is.EqualTo("ref:rootProcessor"));
        Assert.That(rootField.Processors.Single().Processor.FullName, Is.EqualTo("ref:deltaProcessor"));
    }
}
