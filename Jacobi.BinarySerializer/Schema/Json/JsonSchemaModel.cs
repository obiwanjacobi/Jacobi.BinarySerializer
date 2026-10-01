using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jacobi.BinarySerializer.Schema.Json;

internal sealed class JsonSchema
{
    public required string Name { get; init; }
    public IReadOnlyList<JsonSchemaNode> Children { get; init; } = [];
    public IReadOnlyList<JsonSchemaNode> TypeDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaCodecRef> CodecDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaDocumentRef> Includes { get; init; } = [];
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JsonSchemaFieldNode), "field")]
[JsonDerivedType(typeof(JsonSchemaGroupNode), "group")]
[JsonDerivedType(typeof(JsonSchemaRepeatNode), "repeat")]
[JsonDerivedType(typeof(JsonSchemaChoiceNode), "choice")]
internal abstract class JsonSchemaNode
{
    public required string Name { get; init; }
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal sealed class JsonSchemaFieldNode : JsonSchemaNode
{
    public required JsonSchemaCodecRef Codec { get; init; }
    public required SchemaDataType Type { get; init; }
}

internal class JsonSchemaGroupNode : JsonSchemaNode
{
    public IReadOnlyList<JsonSchemaCodecRef> Pipeline { get; init; } = [];
    public IReadOnlyList<JsonSchemaNode> Children { get; init; } = [];
}

internal sealed class JsonSchemaRepeatNode : JsonSchemaGroupNode
{
    public JsonSchemaCodecOrValue<int> Count { get; init; }
}

internal sealed class JsonSchemaChoiceNode : JsonSchemaGroupNode
{
    public JsonSchemaCodecOrValue<int> SelectedIndex { get; init; }
}

public union JsonSchemaCodecOrValue<T>(JsonSchemaCodecRef, T) { }

internal sealed class JsonSchemaCodecRef
{
    public required string Codec { get; init; }
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];
}

internal sealed class JsonSchemaDocumentRef
{
    public required string Schema { get; init; }
    public string? Path { get; init; }
}

internal sealed class JsonSchemaProperty
{
    public required string Name { get; init; }
    public JsonElement Value { get; init; }
}
