using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jacobi.BinarySerializer.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Jacobi.BinarySerializer.Yaml;

/// <summary>
/// Reads and writes schema documents as YAML.
/// The YAML mirrors the JSON schema format and is converted to/from it.
/// </summary>
public static class YamlSerializer
{
    public static SchemaDocument Deserialize(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return Schema.Json.JsonSerializer.Deserialize(YamlToJson(yaml));
    }

    public static string Serialize(SchemaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonToYaml(Schema.Json.JsonSerializer.Serialize(document));
    }

    internal static string YamlToJson(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count == 0)
        {
            throw new InvalidOperationException("Failed to deserialize YAML schema.");
        }

        var node = ToJsonNode(stream.Documents[0].RootNode);
        return node?.ToJsonString() ?? "null";
    }

    internal static string JsonToYaml(string json)
    {
        var node = JsonNode.Parse(json);
        var document = new YamlDocument(FromJsonNode(node));
        var stream = new YamlStream(document);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);

        // YamlStream.Save appends a document end marker ("...").
        return writer.ToString().Replace("...\n", "").Replace("...\r\n", "");
    }

    private static JsonNode? ToJsonNode(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    var name = (key as YamlScalarNode)?.Value
                        ?? throw new InvalidOperationException("YAML mapping keys must be scalars.");
                    obj[name] = ToJsonNode(value);
                }
                return obj;

            case YamlSequenceNode sequence:
                var array = new JsonArray();
                foreach (var item in sequence.Children)
                {
                    array.Add(ToJsonNode(item));
                }
                return array;

            case YamlScalarNode scalar:
                return ToJsonScalar(scalar);

            default:
                throw new InvalidOperationException($"Unsupported YAML node '{node.GetType().Name}'.");
        }
    }

    private static JsonNode? ToJsonScalar(YamlScalarNode scalar)
    {
        var text = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain || text is null)
        {
            return text is null ? null : JsonValue.Create(text);
        }

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (Int64.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && Double.IsFinite(d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(text);
    }

    private static YamlNode FromJsonNode(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return new YamlScalarNode("null");

            case JsonObject obj:
                var mapping = new YamlMappingNode();
                foreach (var (key, value) in obj)
                {
                    mapping.Add(new YamlScalarNode(key), FromJsonNode(value));
                }
                return mapping;

            case JsonArray array:
                var sequence = new YamlSequenceNode();
                foreach (var item in array)
                {
                    sequence.Add(FromJsonNode(item));
                }
                return sequence;

            default:
                var element = node.GetValue<JsonElement>();
                return element.ValueKind switch
                {
                    JsonValueKind.String => QuotedIfAmbiguous(element.GetString()!),
                    JsonValueKind.True => new YamlScalarNode("true"),
                    JsonValueKind.False => new YamlScalarNode("false"),
                    _ => new YamlScalarNode(element.GetRawText())
                };
        }
    }

    private static YamlScalarNode QuotedIfAmbiguous(string text)
    {
        var node = new YamlScalarNode(text);
        // plain scalars that would read back as a non-string (or are unsafe) need quoting.
        var readsAsString = ToJsonScalar(new YamlScalarNode(text)) is JsonValue v
            && v.GetValueKind() == JsonValueKind.String;
        if (!readsAsString || NeedsQuotes(text))
        {
            node.Style = ScalarStyle.DoubleQuoted;
        }
        return node;
    }

    private static bool NeedsQuotes(string text)
        => text.Length == 0
        || text != text.Trim()
        || text.Contains('\n')
        || text.Contains(": ")
        || text.Contains(" #")
        || "-?:,[]{}#&*!|>'\"%@`".Contains(text[0]);
}
