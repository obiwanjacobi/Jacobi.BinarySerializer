using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jacobi.BinarySerializer.Schema.Json;

internal static class JsonSerializer
{
    public static SchemaDocument Deserialize(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowOutOfOrderMetadataProperties = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };
        var jsonSchema = System.Text.Json.JsonSerializer.Deserialize<JsonSchema>(json, options)
            ?? throw new InvalidOperationException("Failed to deserialize JSON schema.");
        var schema = JsonSchemaMapper.ToSchema(jsonSchema);

        return SchemaDocumentMapper.ToDocument(schema);
    }

    public static string Serialize(SchemaDocument document)
    {
        var jsonSchema = JsonSchemaMapper.FromSchema(document);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            AllowOutOfOrderMetadataProperties = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        return System.Text.Json.JsonSerializer.Serialize(jsonSchema, options);
    }
}
