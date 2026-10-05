using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jacobi.BinarySerializer.Schema.Json;

internal sealed class JsonSchema
{
    public required string Name { get; init; }
    public IReadOnlyList<JsonSchemaNode> Children { get; init; } = [];
    public IReadOnlyList<JsonSchemaTypeDef> TypeDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaProcessorDef> ProcessorDefs { get; init; } = [];
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
[JsonDerivedType(typeof(JsonSchemaField), "field")]
[JsonDerivedType(typeof(JsonSchemaGroup), "group")]
[JsonDerivedType(typeof(JsonSchemaRepeat), "repeat")]
[JsonDerivedType(typeof(JsonSchemaChoice), "choice")]
internal abstract class JsonSchemaNode
{
    public required string Name { get; init; }
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal sealed class JsonSchemaField : JsonSchemaNode
{
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    public required SchemaDataType Type { get; init; }
    public JsonSchemaValueOrRef<string> Value { get; init; }
}

internal class JsonSchemaGroup : JsonSchemaNode
{
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    public IReadOnlyList<JsonSchemaNode> Children { get; init; } = [];
}

internal sealed class JsonSchemaRepeat : JsonSchemaGroup
{
    public JsonSchemaValueOrRef<int> Count { get; init; }
}

internal sealed class JsonSchemaChoice : JsonSchemaGroup
{
    public JsonSchemaValueOrRef<int> SelectedIndex { get; init; }
}

/// <summary>A value reference object ({ "reference": "..." }) or a constant.</summary>
public union JsonSchemaValueOrRef<T>(JsonSchemaValueRef, T) { }

internal sealed class JsonSchemaValueRef
{
    public required string Reference { get; init; }
}

/// <summary>A named processor declaration: 'name' is used in a 'ref:name' (or 'ref:document.name').</summary>
internal sealed class JsonSchemaProcessorDef
{
    public required string Name { get; init; }
    public required string Processor { get; init; }
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];
}

/// <summary>A processor key ('namespace.id') or a reference to a processor declaration ('ref:name').</summary>
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
