using System.Globalization;
using System.Text.Json;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema.Json;

internal static class JsonSchemaMapper
{
    public static Schema ToSchema(JsonSchema jsonSchema)
    {
        var children = new List<SchemaNode>();
        var schema = new Schema
        {
            Name = jsonSchema.Name,
            ChildList = children,
            TypeDefs = jsonSchema.TypeDefs.Select(ToSchemaTypeDef).ToList(),
            ProcessorDefs = jsonSchema.ProcessorDefs.Select(ToSchemaProcessorDef).ToList(),
            Includes = jsonSchema.Includes.Select(include => new SchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            PropertyList = MergeProperties(jsonSchema.Properties, jsonSchema.AdditionalData)
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
            TypeDefs = schema.TypeDefs.Select(FromSchemaTypeDef).ToList(),
            ProcessorDefs = schema.ProcessorDefs.Select(FromSchemaProcessorDef).ToList(),
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
        if (jsonNode is JsonSchemaField fieldNode)
        {
            if (!parentAllowsField)
            {
                throw new JsonException("Field nodes must have a group parent.");
            }

            return ToSchemaField(fieldNode);
        }

        if (jsonNode is JsonSchemaGroup groupNode)
        {
            return ToSchemaGroup(groupNode);
        }

        throw new JsonException($"Unsupported JSON schema node type '{jsonNode.GetType().Name}'.");
    }

    private static SchemaField ToSchemaField(JsonSchemaField jsonField)
    {
        return new SchemaField
        {
            Name = jsonField.Name,
            ProcessorsList = jsonField.Processors.Select(ToSchemaProcessorRef).ToList(),
            Type = jsonField.Type,
            Value = ToSchemaValueOrRef(jsonField.Value),
            PropertyList = MergeProperties(jsonField.Properties, jsonField.AdditionalData)
        };
    }

    private static SchemaTypeDef ToSchemaTypeDef(JsonSchemaTypeDef jsonTypeDef)
    {
        return new SchemaTypeDef
        {
            Name = jsonTypeDef.Name,
            Type = jsonTypeDef.Type,
            Processors = jsonTypeDef.Processors.Select(ToSchemaProcessorRef).ToList(),
            PropertyList = MergeProperties(jsonTypeDef.Properties, jsonTypeDef.AdditionalData)
        };
    }

    private static SchemaProcessorDef ToSchemaProcessorDef(JsonSchemaProcessorDef processor)
    {
        return new SchemaProcessorDef
        {
            Name = processor.Name,
            Processor = new ProcessorKey(processor.Processor),
            PropertyList = processor.Properties.Select(ToSchemaProperty).ToList()
        };
    }

    private static SchemaProcessorRef ToSchemaProcessorRef(JsonSchemaProcessorRef processor)
    {
        return new SchemaProcessorRef
        {
            Processor = new SchemaProcessorName(processor.Processor),
            PropertyList = processor.Properties.Select(ToSchemaProperty).ToList()
        };
    }

    private static SchemaGroup ToSchemaGroup(JsonSchemaGroup jsonGroup)
    {
        var children = new List<SchemaNode>();

        SchemaGroup group;
        if (jsonGroup is JsonSchemaRepeat repeat)
        {
            group = new SchemaRepeat
            {
                Name = repeat.Name,
                ProcessorsList = repeat.Processors.Select(ToSchemaProcessorRef).ToList(),
                ChildList = children,
                Count = ToSchemaValueOrRef(repeat.Count),
                ValueProcessorsList = repeat.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(repeat.Properties, repeat.AdditionalData)
            };
        }
        else if (jsonGroup is JsonSchemaChoice choice)
        {
            group = new SchemaChoice
            {
                Name = choice.Name,
                ProcessorsList = choice.Processors.Select(ToSchemaProcessorRef).ToList(),
                ChildList = children,
                SelectedIndex = ToSchemaValueOrRef(choice.SelectedIndex),
                ValueProcessorsList = choice.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(choice.Properties, choice.AdditionalData)
            };
        }
        else
        {
            group = new SchemaRepeat
            {
                Name = jsonGroup.Name,
                ProcessorsList = jsonGroup.Processors.Select(ToSchemaProcessorRef).ToList(),
                ChildList = children,
                Count = 1,
                PropertyList = MergeProperties(jsonGroup.Properties, jsonGroup.AdditionalData)
            };
        }

        foreach (var child in jsonGroup.Children)
        {
            children.Add(ToSchemaNode(child, true));
        }

        return group;
    }

    private static SchemaValueOrRef<int> ToSchemaValueOrRef(JsonSchemaValueOrRef<int> valueOrRef)
    {
        if (valueOrRef is JsonSchemaValueRef valueRef)
        {
            if (SchemaNodeRef.TryParse(valueRef.Reference, out var nodeRef))
            {
                return nodeRef;
            }

            if (SchemaPubRef.TryParse(valueRef.Reference, out var pubRef))
            {
                return pubRef;
            }

            throw new JsonException($"Invalid value reference '{valueRef.Reference}'. Expected 'ref:path' or 'pub:namespace.name'.");
        }

        if (valueOrRef is int value)
        {
            return value;
        }

        throw new JsonException($"Unsupported JSON schema value type '{valueOrRef.GetType().Name}'.");
    }

    private static JsonSchemaNode FromSchemaNode(SchemaNode schemaNode)
    {
        return schemaNode switch
        {
            SchemaField field => new JsonSchemaField
            {
                Name = field.Name,
                Processors = field.Processors.Select(FromSchemaProcessorRef).ToList(),
                Type = field.Type,
                Value = FromSchemaValueOrRef(field.Value),
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaGroup group => CreateJsonGroupNode(group),
            _ => throw new JsonException($"Unsupported schema node type '{schemaNode.GetType().Name}'.")
        };
    }

    private static JsonSchemaTypeDef FromSchemaTypeDef(SchemaTypeDef typeDef)
    {
        return new JsonSchemaTypeDef
        {
            Name = typeDef.Name,
            Type = typeDef.Type,
            Processors = typeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
            Properties = typeDef.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static JsonSchemaGroup CreateJsonGroupNode(SchemaGroup group)
    {
        if (group is SchemaRepeat repeat)
        {
            return new JsonSchemaRepeat
            {
                Name = repeat.Name,
                Processors = repeat.Processors.Select(FromSchemaProcessorRef).ToList(),
                Children = repeat.Children.Select(FromSchemaNode).ToList(),
                Count = FromSchemaValueOrRef(repeat.Count),
                ValueProcessors = repeat.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = repeat.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (group is SchemaChoice choice)
        {
            return new JsonSchemaChoice
            {
                Name = choice.Name,
                Processors = choice.Processors.Select(FromSchemaProcessorRef).ToList(),
                Children = choice.Children.Select(FromSchemaNode).ToList(),
                SelectedIndex = FromSchemaValueOrRef(choice.SelectedIndex),
                ValueProcessors = choice.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        throw new JsonException($"Unsupported schema group type '{group.GetType().Name}'.");
    }

    private static JsonSchemaValueOrRef<int> FromSchemaValueOrRef(SchemaValueOrRef<int> valueOrRef)
    {
        if (valueOrRef is SchemaNodeRef nodeRef)
        {
            return new JsonSchemaValueRef { Reference = nodeRef.ToString() };
        }

        if (valueOrRef is SchemaPubRef pubRef)
        {
            return new JsonSchemaValueRef { Reference = pubRef.ToString() };
        }

        if (valueOrRef is int value)
        {
            return value;
        }

        throw new JsonException($"Unsupported schema value type '{valueOrRef.GetType().Name}'.");
    }

    /// <summary>An unset value (optional) maps to the default (unset) value.</summary>
    private static SchemaValueOrRef<string> ToSchemaValueOrRef(JsonSchemaValueOrRef<string> valueOrRef)
    {
        if (valueOrRef is JsonSchemaValueRef valueRef)
        {
            if (SchemaNodeRef.TryParse(valueRef.Reference, out var nodeRef))
            {
                return nodeRef;
            }

            if (SchemaPubRef.TryParse(valueRef.Reference, out var pubRef))
            {
                return pubRef;
            }

            throw new JsonException($"Invalid value reference '{valueRef.Reference}'. Expected 'ref:path' or 'pub:namespace.name'.");
        }

        return valueOrRef is string value ? value : default(SchemaValueOrRef<string>);
    }

    private static JsonSchemaValueOrRef<string> FromSchemaValueOrRef(SchemaValueOrRef<string> valueOrRef)
        => valueOrRef switch
        {
            SchemaNodeRef nodeRef => new JsonSchemaValueRef { Reference = nodeRef.ToString() },
            SchemaPubRef pubRef => new JsonSchemaValueRef { Reference = pubRef.ToString() },
            string value => value,
            _ => default,
        };

    private static JsonSchemaProcessorDef FromSchemaProcessorDef(SchemaProcessorDef processorDef)
    {
        return new JsonSchemaProcessorDef
        {
            Name = processorDef.Name,
            Processor = processorDef.Processor.ToString(),
            Properties = processorDef.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static JsonSchemaProcessorRef FromSchemaProcessorRef(SchemaProcessorRef processorRef)
    {
        return new JsonSchemaProcessorRef
        {
            Processor = processorRef.Processor.ToString(),
            Properties = processorRef.Properties.Select(FromSchemaProperty).ToList()
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
