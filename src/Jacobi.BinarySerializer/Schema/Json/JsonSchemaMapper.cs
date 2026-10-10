using System.Globalization;
using System.Text.Json;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema.Json;

internal static class JsonSchemaMapper
{
    public static Schema ToSchema(JsonSchema jsonSchema)
    {
        var members = new List<SchemaNode>();
        var schema = new Schema
        {
            Name = jsonSchema.Name,
            MemberList = members,
            NodeDefs = jsonSchema.NodeDefs.Select(ToSchemaNodeDef).ToList(),
            DataTypeDefs = jsonSchema.DataTypeDefs.Select(ToSchemaDataTypeDef).ToList(),
            ProcessorDefs = jsonSchema.ProcessorDefs.Select(ToSchemaProcessorDef).ToList(),
            Includes = jsonSchema.Includes.Select(include => new SchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            PropertyList = MergeProperties(jsonSchema.Properties, jsonSchema.AdditionalData)
        };

        foreach (var jsonNode in jsonSchema.Members)
        {
            var node = ToSchemaNode(jsonNode, false);
            if (node is not SchemaGroup group)
            {
                throw new JsonException("Schema members must be group nodes.");
            }

            members.Add(group);
        }

        return schema;
    }

    public static JsonSchema FromSchema(Schema schema)
    {
        return new JsonSchema
        {
            Name = schema.Name,
            Members = schema.Members.Select(FromSchemaNode).ToList(),
            NodeDefs = schema.NodeDefs.Select(FromSchemaNodeDef).ToList(),
            DataTypeDefs = schema.DataTypeDefs.Select(FromSchemaDataTypeDef).ToList(),
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
            NodeDef = ToNodeDefName(jsonField.NodeDef),
            ProcessorsList = jsonField.Processors.Select(ToSchemaProcessorRef).ToList(),
            DataType = jsonField.DataType,
            Value = ToSchemaValueOrRef(jsonField.Value),
            ByteLength = ToSchemaValueOrRef(jsonField.ByteLength),
            ByteOffset = jsonField.ByteOffset,
            PropertyList = MergeProperties(jsonField.Properties, jsonField.AdditionalData)
        };
    }

    private static SchemaName? ToNodeDefName(string? nodeDef)
    {
        if (String.IsNullOrWhiteSpace(nodeDef))
        {
            return null;
        }
        return new SchemaName(nodeDef);
    }

    private static SchemaNodeDef ToSchemaNodeDef(JsonSchemaNodeDef jsonNodeDef)
    {
        return new SchemaNodeDef
        {
            Name = jsonNodeDef.Name,
            DataType = jsonNodeDef.DataType,
            Processors = jsonNodeDef.Processors.Select(ToSchemaProcessorRef).ToList(),
            PropertyList = MergeProperties(jsonNodeDef.Properties, jsonNodeDef.AdditionalData)
        };
    }

    private static SchemaDataTypeDef ToSchemaDataTypeDef(JsonSchemaDataTypeDef jsonDef)
    {
        return new SchemaDataTypeDef
        {
            Name = jsonDef.Name,
            BasedOn = jsonDef.BasedOn,
            Min = jsonDef.Min,
            Max = jsonDef.Max,
            Scale = jsonDef.Scale,
            Shift = jsonDef.Shift,
            Options = jsonDef.Options?.ToDictionary(o => Int64.Parse(o.Key, CultureInfo.InvariantCulture), o => o.Value),
            Processors = jsonDef.Processors.Select(ToSchemaProcessorRef).ToList(),
            PropertyList = MergeProperties(jsonDef.Properties, jsonDef.AdditionalData)
        };
    }

    private static JsonSchemaDataTypeDef FromSchemaDataTypeDef(SchemaDataTypeDef def)
    {
        return new JsonSchemaDataTypeDef
        {
            Name = def.Name,
            BasedOn = def.BasedOn,
            Min = def.Min,
            Max = def.Max,
            Scale = def.Scale,
            Shift = def.Shift,
            Options = def.Options?.ToDictionary(o => o.Key.ToString(CultureInfo.InvariantCulture), o => o.Value),
            Processors = def.Processors.Select(FromSchemaProcessorRef).ToList(),
            Properties = def.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static SchemaProcessorDef ToSchemaProcessorDef(JsonSchemaProcessorDef processor)
    {
        return new SchemaProcessorDef
        {
            Name = processor.Name,
            Processor = new ProcessorKey(processor.Processor),
            PropertyList = MergeProperties(processor.Properties, processor.AdditionalData)
        };
    }

    private static SchemaProcessorRef ToSchemaProcessorRef(JsonSchemaProcessorRef processor)
    {
        return new SchemaProcessorRef
        {
            Processor = new SchemaProcessorName(processor.Processor),
            PublishNamespace = processor.PubNs,
            PropertyList = MergeProperties(processor.Properties, processor.AdditionalData)
        };
    }

    private static SchemaGroup ToSchemaGroup(JsonSchemaGroup jsonGroup)
    {
        var members = new List<SchemaNode>();

        SchemaGroup group;
        if (jsonGroup is JsonSchemaRepeat repeat)
        {
            group = new SchemaRepeat
            {
                Name = repeat.Name,
                NodeDef = ToNodeDefName(repeat.NodeDef),
                ProcessorsList = repeat.Processors.Select(ToSchemaProcessorRef).ToList(),
                MemberList = members,
                Count = ToSchemaValueOrRef(repeat.Count),
                ByteSize = ToSchemaValueOrRef(repeat.ByteSize),
                ValueProcessorsList = repeat.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(repeat.Properties, repeat.AdditionalData)
            };
        }
        else if (jsonGroup is JsonSchemaChoice choice)
        {
            group = new SchemaChoice
            {
                Name = choice.Name,
                NodeDef = ToNodeDefName(choice.NodeDef),
                ProcessorsList = choice.Processors.Select(ToSchemaProcessorRef).ToList(),
                MemberList = members,
                SelectedIndex = ToSchemaValueOrRef(choice.SelectedIndex),
                ByteSize = ToSchemaValueOrRef(choice.ByteSize),
                ValueProcessorsList = choice.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(choice.Properties, choice.AdditionalData)
            };
        }
        else
        {
            group = new SchemaRepeat
            {
                Name = jsonGroup.Name,
                NodeDef = ToNodeDefName(jsonGroup.NodeDef),
                ProcessorsList = jsonGroup.Processors.Select(ToSchemaProcessorRef).ToList(),
                MemberList = members,
                Count = 1,
                ByteSize = ToSchemaValueOrRef(jsonGroup.ByteSize),
                PropertyList = MergeProperties(jsonGroup.Properties, jsonGroup.AdditionalData)
            };
        }

        foreach (var child in jsonGroup.Members)
        {
            members.Add(ToSchemaNode(child, true));
        }

        return group;
    }

    private static SchemaValueOrRef<int> ToSchemaValueOrRef(JsonSchemaValueOrRef<int> valueOrRef)
    {
        if (valueOrRef is JsonSchemaValueRef valueRef)
        {
            var parsed = ParseValueRef(valueRef);
            if (parsed is SchemaNodeRef nodeRef)
            {
                return nodeRef;
            }

            if (parsed is SchemaPubRef pubRef)
            {
                return pubRef;
            }

            return default;
        }

        if (valueOrRef is int value)
        {
            return value;
        }

        return default;
    }

    private static JsonSchemaNode FromSchemaNode(SchemaNode schemaNode)
    {
        return schemaNode switch
        {
            SchemaField field => new JsonSchemaField
            {
                Name = field.Name,
                NodeDef = field.NodeDef?.ToString(),
                Processors = field.Processors.Select(FromSchemaProcessorRef).ToList(),
                DataType = field.DataType,
                Value = FromSchemaValueOrRef(field.Value),
                ByteLength = FromSchemaValueOrRef(field.ByteLength),
                ByteOffset = field.ByteOffset,
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaGroup group => CreateJsonGroupNode(group),
            _ => throw new JsonException($"Unsupported schema node type '{schemaNode.GetType().Name}'.")
        };
    }

    private static JsonSchemaNodeDef FromSchemaNodeDef(SchemaNodeDef nodeDef)
    {
        return new JsonSchemaNodeDef
        {
            Name = nodeDef.Name,
            DataType = nodeDef.DataType,
            Processors = nodeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
            Properties = nodeDef.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static JsonSchemaGroup CreateJsonGroupNode(SchemaGroup group)
    {
        if (group is SchemaRepeat repeat)
        {
            return new JsonSchemaRepeat
            {
                Name = repeat.Name,
                NodeDef = repeat.NodeDef?.ToString(),
                Processors = repeat.Processors.Select(FromSchemaProcessorRef).ToList(),
                Members = repeat.Members.Select(FromSchemaNode).ToList(),
                Count = FromSchemaValueOrRef(repeat.Count),
                ByteSize = FromSchemaValueOrRef(repeat.ByteSize),
                ValueProcessors = repeat.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = repeat.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (group is SchemaChoice choice)
        {
            return new JsonSchemaChoice
            {
                Name = choice.Name,
                NodeDef = choice.NodeDef?.ToString(),
                Processors = choice.Processors.Select(FromSchemaProcessorRef).ToList(),
                Members = choice.Members.Select(FromSchemaNode).ToList(),
                SelectedIndex = FromSchemaValueOrRef(choice.SelectedIndex),
                ByteSize = FromSchemaValueOrRef(choice.ByteSize),
                ValueProcessors = choice.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        throw new JsonException($"Unsupported schema group type '{group.GetType().Name}'.");
    }

    /// <summary>The 'ref' and 'pub' keys are mutually exclusive; neither means unset (null).</summary>
    private static object? ParseValueRef(JsonSchemaValueRef valueRef)
    {
        if (valueRef.Ref is not null && valueRef.Pub is not null)
        {
            throw new JsonException("A value reference cannot specify both 'ref' and 'pub'.");
        }

        if (valueRef.Ref is not null)
        {
            return SchemaNodeRef.TryParse(SchemaNodeRef.Prefix + valueRef.Ref, out var nodeRef)
                ? nodeRef
                : throw new JsonException($"Invalid node reference '{valueRef.Ref}'.");
        }

        if (valueRef.Pub is not null)
        {
            return SchemaPubRef.TryParse(SchemaPubRef.Prefix + valueRef.Pub, out var pubRef)
                ? pubRef
                : throw new JsonException($"Invalid published reference '{valueRef.Pub}'. Expected 'namespace.name'.");
        }

        return null;
    }

    private static JsonSchemaValueRef FromNodeRef(SchemaNodeRef nodeRef)
        => new() { Ref = nodeRef.ToString()[SchemaNodeRef.Prefix.Length..] };

    private static JsonSchemaValueRef FromPubRef(SchemaPubRef pubRef)
        => new() { Pub = pubRef.ToString()[SchemaPubRef.Prefix.Length..] };

    private static JsonSchemaValueOrRef<int> FromSchemaValueOrRef(SchemaValueOrRef<int> valueOrRef)
    {
        if (valueOrRef is SchemaNodeRef nodeRef)
        {
            return FromNodeRef(nodeRef);
        }

        if (valueOrRef is SchemaPubRef pubRef)
        {
            return FromPubRef(pubRef);
        }

        if (valueOrRef is int value)
        {
            return value;
        }

        return default(JsonSchemaValueOrRef<int>);
    }

    /// <summary>An unset value (optional) maps to the default (unset) value.</summary>
    private static SchemaValueOrRef<string> ToSchemaValueOrRef(JsonSchemaValueOrRef<string> valueOrRef)
    {
        if (valueOrRef is JsonSchemaValueRef valueRef)
        {
            var parsed = ParseValueRef(valueRef);
            if (parsed is SchemaNodeRef nodeRef)
            {
                return nodeRef;
            }

            if (parsed is SchemaPubRef pubRef)
            {
                return pubRef;
            }

            return default;
        }

        return valueOrRef is string value ? value : default(SchemaValueOrRef<string>);
    }

    private static JsonSchemaValueOrRef<string> FromSchemaValueOrRef(SchemaValueOrRef<string> valueOrRef)
        => valueOrRef switch
        {
            SchemaNodeRef nodeRef => FromNodeRef(nodeRef),
            SchemaPubRef pubRef => FromPubRef(pubRef),
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
            PubNs = processorRef.PublishNamespace,
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

    private static string? ValueAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number when element.TryGetInt64(out var int64) => int64.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Number => element.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }

    private static JsonElement ToJsonElement(string? value)
    {
        return System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
}
