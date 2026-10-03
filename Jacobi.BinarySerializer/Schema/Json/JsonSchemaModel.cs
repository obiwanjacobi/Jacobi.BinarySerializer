using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jacobi.BinarySerializer.Schema.Json;

internal sealed class JsonSchema
{
    public required string Name { get; init; }
    public IReadOnlyList<JsonSchemaNode> Children { get; init; } = [];
    public IReadOnlyList<JsonSchemaTypeDef> TypeDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaProcessorRef> ProcessorDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaDocumentRef> Includes { get; init; } = [];
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal sealed class JsonSchemaTypeDef : JsonSchemaNode
{
    public required IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; }
    public SchemaDataType Type { get; init; } = SchemaDataType.None;
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
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    public required SchemaDataType Type { get; init; }
}

internal class JsonSchemaGroupNode : JsonSchemaNode
{
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    public IReadOnlyList<JsonSchemaNode> Children { get; init; } = [];
}

internal sealed class JsonSchemaRepeatNode : JsonSchemaGroupNode
{
    public JsonSchemaValueOrRef<int> Count { get; init; }
}

internal sealed class JsonSchemaChoiceNode : JsonSchemaGroupNode
{
    public JsonSchemaValueOrRef<int> SelectedIndex { get; init; }
}

/// <summary>A value reference object ({ "reference": "..." }) or a constant.</summary>
public union JsonSchemaValueOrRef<T>(JsonSchemaValueRef, T) { }

internal sealed class JsonSchemaValueRef
{
    public required string Reference { get; init; }
}

internal sealed class JsonSchemaProcessorRef
{
    public required string Processor { get; init; }
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
