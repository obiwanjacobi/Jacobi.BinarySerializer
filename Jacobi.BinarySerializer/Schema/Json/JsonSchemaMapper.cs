using System.Globalization;
using System.Text.Json;

namespace Jacobi.BinarySerializer.Schema.Json;

internal static class JsonSchemaMapper
{
    public static Schema ToSchema(JsonSchema jsonSchema)
    {
        var children = new List<SchemaGroup>();
        var schema = new Schema
        {
            Name = jsonSchema.Name,
            Children = children,
            TypeDefs = jsonSchema.TypeDefs.Select(node => ToSchemaNode(node, true)).ToList(),
            CodecDefs = jsonSchema.CodecDefs.Select(ToSchemaCodecRef).ToList(),
            Includes = jsonSchema.Includes.Select(include => new SchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            Properties = MergeProperties(jsonSchema.Properties, jsonSchema.AdditionalData)
        };

        foreach (var jsonNode in jsonSchema.Children)
        {
            var node = ToSchemaNode(jsonNode, false);
            if (node is not SchemaGroup group)
            {
                throw new JsonException("Schema children must be group nodes.");
            }

            children.Add(group);
        }

        return schema;
    }

    public static JsonSchema FromSchema(Schema schema)
    {
        return new JsonSchema
        {
            Name = schema.Name,
            Children = schema.Children.Select(FromSchemaNode).ToList(),
            TypeDefs = schema.TypeDefs.Select(FromSchemaNode).ToList(),
            CodecDefs = schema.CodecDefs.Select(FromSchemaCodecRef).ToList(),
            Includes = schema.Includes.Select(include => new JsonSchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            Properties = schema.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static SchemaNode ToSchemaNode(JsonSchemaNode jsonNode, bool parentAllowsField)
    {
        return jsonNode switch
        {
            JsonSchemaFieldNode fieldNode when parentAllowsField => ToSchemaField(fieldNode),
            JsonSchemaFieldNode => throw new JsonException("Field nodes must have a group parent."),
            JsonSchemaGroupNode groupNode => ToSchemaGroup(groupNode),
            _ => throw new JsonException($"Unsupported JSON schema node type '{jsonNode.GetType().Name}'.")
        };
    }

    private static SchemaField ToSchemaField(JsonSchemaFieldNode jsonField)
    {
        return new SchemaField
        {
            Name = jsonField.Name,
            Codec = ToSchemaCodecRef(jsonField.Codec),
            Type = jsonField.Type,
            Properties = MergeProperties(jsonField.Properties, jsonField.AdditionalData)
        };
    }

    private static SchemaCodecRef ToSchemaCodecRef(JsonSchemaCodecRef codec)
    {
        return new SchemaCodecRef
        {
            Codec = codec.Codec,
            Properties = codec.Properties.Select(ToSchemaProperty).ToList()
        };
    }

    private static SchemaGroup ToSchemaGroup(JsonSchemaGroupNode jsonGroup)
    {
        var children = new List<SchemaNode>();
        var group = jsonGroup switch
        {
            JsonSchemaRepeatNode repeat => new SchemaRepeat
            {
                Name = repeat.Name,
                Pipeline = repeat.Pipeline.Select(ToSchemaCodecRef).ToList(),
                Children = children,
                Count = ToSchemaCodecOrValue(repeat.Count),
                Properties = MergeProperties(repeat.Properties, repeat.AdditionalData)
            },
            JsonSchemaChoiceNode choice => new SchemaChoice
            {
                Name = choice.Name,
                Pipeline = choice.Pipeline.Select(ToSchemaCodecRef).ToList(),
                Children = children,
                SelectedIndex = ToSchemaCodecOrValue(choice.SelectedIndex),
                Properties = MergeProperties(choice.Properties, choice.AdditionalData)
            },
            _ => new SchemaGroup
            {
                Name = jsonGroup.Name,
                Pipeline = jsonGroup.Pipeline.Select(ToSchemaCodecRef).ToList(),
                Children = children,
                Properties = MergeProperties(jsonGroup.Properties, jsonGroup.AdditionalData)
            }
        };

        foreach (var child in jsonGroup.Children)
        {
            children.Add(ToSchemaNode(child, true));
        }

        return group;
    }

    private static SchemaCodecOrValue<int> ToSchemaCodecOrValue(JsonSchemaCodecOrValue<int> codecOrValue)
    {
        return codecOrValue switch
        {
            JsonSchemaCodecRef codec => ToSchemaCodecRef(codec),
            int value => value,
            _ => throw new JsonException($"Unsupported JSON schema codec or value type '{codecOrValue.GetType().Name}'.")
        };
    }

    private static JsonSchemaNode FromSchemaNode(SchemaNode schemaNode)
    {
        return schemaNode switch
        {
            SchemaField field => new JsonSchemaFieldNode
            {
                Name = field.Name,
                Codec = FromSchemaCodecRef(field.Codec),
                Type = field.Type,
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaGroup group => CreateJsonGroupNode(group),
            _ => throw new JsonException($"Unsupported schema node type '{schemaNode.GetType().Name}'.")
        };
    }

    private static JsonSchemaGroupNode CreateJsonGroupNode(SchemaGroup group)
    {
        return group switch
        {
            SchemaRepeat repeat => new JsonSchemaRepeatNode
            {
                Name = repeat.Name,
                Pipeline = repeat.Pipeline.Select(FromSchemaCodecRef).ToList(),
                Children = repeat.Children.Select(FromSchemaNode).ToList(),
                Count = FromSchemaCodecOrValue(repeat.Count),
                Properties = repeat.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaChoice choice => new JsonSchemaChoiceNode
            {
                Name = choice.Name,
                Pipeline = choice.Pipeline.Select(FromSchemaCodecRef).ToList(),
                Children = choice.Children.Select(FromSchemaNode).ToList(),
                SelectedIndex = FromSchemaCodecOrValue(choice.SelectedIndex),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            },
            _ => new JsonSchemaGroupNode
            {
                Name = group.Name,
                Pipeline = group.Pipeline.Select(FromSchemaCodecRef).ToList(),
                Children = group.Children.Select(FromSchemaNode).ToList(),
                Properties = group.Properties.Select(FromSchemaProperty).ToList()
            }
        };
    }

    private static JsonSchemaCodecOrValue<int> FromSchemaCodecOrValue(SchemaCodecOrValue<int> codecOrValue)
    {
        return codecOrValue switch
        {
            SchemaCodecRef codec => FromSchemaCodecRef(codec),
            int value => value,
            _ => throw new JsonException($"Unsupported schema codec or value type '{codecOrValue.GetType().Name}'.")
        };
    }

    private static JsonSchemaCodecRef FromSchemaCodecRef(SchemaCodecRef codecRef)
    {
        return new JsonSchemaCodecRef
        {
            Codec = codecRef.Codec,
            Properties = codecRef.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static JsonSchemaProperty FromSchemaProperty(SchemaProperty property)
    {
        return new JsonSchemaProperty
        {
            Name = property.Name,
            Value = ToJsonElement(property.Value)
        };
    }

    private static List<SchemaProperty> MergeProperties(
        IReadOnlyList<JsonSchemaProperty> explicitProperties,
        IDictionary<string, JsonElement>? additionalData)
    {
        var merged = explicitProperties.Select(ToSchemaProperty).ToList();

        if (additionalData is null)
        {
            return merged;
        }

        foreach (var item in additionalData)
        {
            merged.Add(new SchemaProperty
            {
                Name = item.Key,
                Value = ValueAsString(item.Value)
            });
        }

        return merged;
    }

    private static SchemaProperty ToSchemaProperty(JsonSchemaProperty jsonProperty)
    {
        return new SchemaProperty
        {
            Name = jsonProperty.Name,
            Value = ValueAsString(jsonProperty.Value)
        };
    }

    private static string ValueAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => element.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    private static JsonElement ToJsonElement(string value)
    {
        return System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
}
