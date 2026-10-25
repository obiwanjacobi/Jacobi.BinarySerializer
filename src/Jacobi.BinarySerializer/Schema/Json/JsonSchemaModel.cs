using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jacobi.BinarySerializer.Schema.Json;

internal sealed class JsonSchema
{
    public required string Name { get; init; }
    public IReadOnlyList<JsonSchemaNode> Members { get; init; } = [];
    public IReadOnlyList<JsonSchemaNode> NodeDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaDataTypeDef> DataTypeDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaProcessorDef> ProcessorDefs { get; init; } = [];
    public IReadOnlyList<JsonSchemaDocumentRef> Includes { get; init; } = [];
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal sealed class JsonSchemaDataTypeDef
{
    public required string Name { get; init; }
    [JsonPropertyName("basedOn")]
    public required string BasedOn { get; init; }
    [JsonConverter(typeof(JsonLiteralConverter))]
    public string? Min { get; init; }
    [JsonConverter(typeof(JsonLiteralConverter))]
    public string? Max { get; init; }
    public decimal? Scale { get; init; }
    public decimal? Shift { get; init; }
    public IDictionary<string, string>? Options { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ValueType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OptionsDefault { get; init; }
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>A literal written as a JSON string, number or boolean; written back as a number when it is one.</summary>
internal sealed class JsonLiteralConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False => Encoding.UTF8.GetString(reader.ValueSpan),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a literal (string, number or boolean), not {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        if (Decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(JsonSchemaField), "field")]
[JsonDerivedType(typeof(JsonSchemaGroup), "group")]
[JsonDerivedType(typeof(JsonSchemaRepeat), "repeat")]
[JsonDerivedType(typeof(JsonSchemaChoice), "choice")]
internal abstract class JsonSchemaNode
{
    public required string Name { get; init; }

    /// <summary>
    /// Optional reference to a nodeDef that defines the type and processors of the node.
    /// </summary>
    [JsonPropertyName("nodeDef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeDef { get; init; }
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

internal sealed class JsonSchemaField : JsonSchemaNode
{
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    [JsonPropertyName("datatype")]
    public string? DataType { get; init; }
    public JsonSchemaValueOrRef<string> Value { get; init; }
    public JsonSchemaValueOrRef<int> ByteLength { get; init; }
    public int? ByteOffset { get; init; }
}

internal class JsonSchemaGroup : JsonSchemaNode
{
    public IReadOnlyList<JsonSchemaProcessorRef> Processors { get; init; } = [];
    public IReadOnlyList<JsonSchemaNode> Members { get; init; } = [];
    public JsonSchemaValueOrRef<int> ByteSize { get; init; }
}

internal sealed class JsonSchemaRepeat : JsonSchemaGroup
{
    public JsonSchemaValueOrRef<int> Count { get; init; }
    public IReadOnlyList<JsonSchemaProcessorRef> ValueProcessors { get; init; } = [];
}

internal sealed class JsonSchemaChoice : JsonSchemaGroup
{
    public JsonSchemaValueOrRef<int> SelectedIndex { get; init; }

    [JsonPropertyName("memberNodeDef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MemberNodeDef { get; init; }

    [JsonPropertyName("pipeline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pipeline { get; init; }
    public IReadOnlyList<JsonSchemaProcessorRef> ValueProcessors { get; init; } = [];
}

/// <summary>A value reference
public union JsonSchemaValueOrRef<T>(JsonSchemaValueRef, T) { }

internal sealed class JsonSchemaValueRef
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ref { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pub { get; init; }
}

/// <summary>A named processor declaration: 'name' is used as 'name' (or 'document.name' from another document).</summary>
internal sealed class JsonSchemaProcessorDef
{
    public required string Name { get; init; }
    public required string Processor { get; init; }
    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
}

/// <summary>A reference to a processor declaration ('name' or 'document.name') or, when no declaration has that name, a processor key ('namespace.id').</summary>
internal sealed class JsonSchemaProcessorRef
{
    [JsonPropertyName("name")]
    public required string Processor { get; init; }

    [JsonPropertyName("pubns")]
    public string? PubNs { get; init; }

    public IReadOnlyList<JsonSchemaProperty> Properties { get; init; } = [];

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; init; }
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
