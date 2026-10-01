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
                    ""pipeline"": [
                        { ""codec"": ""root"" }
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
                    ""pipeline"": [
                        { ""codec"": ""root"" }
                    ],
                    ""children"": [
                        {
                            ""name"": ""FieldA"",
                            ""kind"": ""field"",
                            ""codec"": { ""codec"": ""identity"" },
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
    public void Deserialize_TypeDefsAndCodecDefs_ArePreservedWithoutResolution()
    {
        var json = @"
        {
            ""name"": ""TestSchema"",
            ""codecDefs"": [
                {
                    ""codec"": ""rootCodec""
                },
                {
                    ""codec"": ""deltaCodec"",
                    ""properties"": [
                        { ""name"": ""bits"", ""value"": ""7"" }
                    ]
                }
            ],
            ""typeDefs"": [
                {
                    ""name"": ""CommonField"",
                    ""kind"": ""field"",
                    ""codec"": { ""codec"": ""deltaCodec"" },
                    ""type"": ""Int32"",
                    ""properties"": [
                        { ""name"": ""scale"", ""value"": ""100"" }
                    ]
                },
                {
                    ""name"": ""CommonGroup"",
                    ""kind"": ""group"",
                    ""pipeline"": [
                        { ""codec"": ""rootCodec"" }
                    ],
                    ""children"": [
                        {
                            ""name"": ""InnerField"",
                            ""kind"": ""field"",
                            ""codec"": { ""codec"": ""deltaCodec"" },
                            ""type"": ""Int16""
                        }
                    ]
                }
            ],
            ""children"": [
                {
                    ""name"": ""RootGroup"",
                    ""kind"": ""group"",
                    ""pipeline"": [
                        { ""codec"": ""rootCodec"" }
                    ],
                    ""children"": [
                        {
                            ""name"": ""Value"",
                            ""kind"": ""field"",
                            ""codec"": { ""codec"": ""deltaCodec"" },
                            ""type"": ""Int32""
                        }
                    ]
                }
            ],
            ""properties"": []
        }";

        var document = JsonSerializer.Deserialize(json);

        Assert.That(document.CodecDefs.Count, Is.EqualTo(2));
        Assert.That(document.CodecDefs.Any(c => c.Codec == "rootCodec"));
        Assert.That(document.CodecDefs.Any(c => c.Codec == "deltaCodec" && c.Properties.Any(p => p.Name == "bits" && p.Value == "7")));

        Assert.That(document.TypeDefs.Count, Is.EqualTo(2));
        Assert.That(document.TypeDefs.OfType<SchemaField>().Any(f => f.Name == "CommonField" && f.Codec.Codec == "deltaCodec"));
        Assert.That(document.TypeDefs.OfType<SchemaGroup>().Any(g => g.Name == "CommonGroup" && g.Pipeline.Any(p => p.Codec == "rootCodec")));

        // Resolver not implemented yet: usage remains as raw references.
        var rootGroup = document.Roots.Single();
        var rootField = rootGroup.Children.OfType<SchemaField>().Single();
        Assert.That(rootGroup.Pipeline.Single().Codec, Is.EqualTo("rootCodec"));
        Assert.That(rootField.Codec.Codec, Is.EqualTo("deltaCodec"));
    }
}
