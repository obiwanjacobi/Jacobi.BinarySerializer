using System.Globalization;
using System.Xml;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema.Xml;

internal static class XmlSchemaMapper
{
    private static readonly HashSet<string> SchemaKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchema.Name),
        nameof(XmlSchema.Members),
        nameof(XmlSchema.TypeDefs),
        nameof(XmlSchema.ProcessorDefs),
        nameof(XmlSchema.Includes),
        nameof(XmlSchema.Properties)
    };

    private static readonly HashSet<string> GroupKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaGroup.Name),
        nameof(XmlSchemaGroup.Processors),
        nameof(XmlSchemaGroup.Members),
        nameof(XmlSchemaGroup.ByteSize),
        nameof(XmlSchemaGroup.ByteSizeRef),
        nameof(XmlSchemaGroup.Properties)
    };

    private static readonly HashSet<string> RepeatKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaRepeat.Name),
        nameof(XmlSchemaRepeat.Processors),
        nameof(XmlSchemaRepeat.Members),
        nameof(XmlSchemaRepeat.Count),
        nameof(XmlSchemaRepeat.CountRef),
        nameof(XmlSchemaRepeat.ByteSize),
        nameof(XmlSchemaRepeat.ByteSizeRef),
        nameof(XmlSchemaRepeat.ValueProcessors),
        nameof(XmlSchemaRepeat.Properties)
    };

    private static readonly HashSet<string> ChoiceKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaChoice.Name),
        nameof(XmlSchemaChoice.Processors),
        nameof(XmlSchemaChoice.Members),
        nameof(XmlSchemaChoice.SelectedIndex),
        nameof(XmlSchemaChoice.SelectedIndexRef),
        nameof(XmlSchemaChoice.ByteSize),
        nameof(XmlSchemaChoice.ByteSizeRef),
        nameof(XmlSchemaChoice.ValueProcessors),
        nameof(XmlSchemaChoice.Properties)
    };

    private static readonly HashSet<string> FieldKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaField.Name),
        nameof(XmlSchemaField.Processors),
        nameof(XmlSchemaField.Type),
        nameof(XmlSchemaField.Value),
        nameof(XmlSchemaField.ValueRef),
        nameof(XmlSchemaField.ByteLength),
        nameof(XmlSchemaField.ByteLengthRef),
        nameof(XmlSchemaField.ByteOffset),
        nameof(XmlSchemaField.Properties)
    };

    private static readonly HashSet<string> ProcessorDefKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaProcessorDef.Name),
        nameof(XmlSchemaProcessorDef.Processor),
        nameof(XmlSchemaProcessorDef.Properties)
    };

    private static readonly HashSet<string> ProcessorRefKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaProcessorRef.Processor),
        nameof(XmlSchemaProcessorRef.PubNs),
        nameof(XmlSchemaProcessorRef.Properties)
    };

    public static Schema ToSchema(XmlSchema xmlSchema)
    {
        var members = new List<SchemaNode>();

        var schema = new Schema
        {
            Name = xmlSchema.Name,
            MemberList = members,
            TypeDefs = xmlSchema.TypeDefs.Select(ToSchemaTypeDef).ToList(),
            ProcessorDefs = xmlSchema.ProcessorDefs.Select(ToSchemaProcessorDef).ToList(),
            Includes = xmlSchema.Includes.Select(include => new SchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            PropertyList = MergeProperties(
                xmlSchema.Properties,
                xmlSchema.AdditionalAttributes,
                xmlSchema.AdditionalElements,
                SchemaKnownNames)
        };

        foreach (var child in xmlSchema.Members)
        {
            var node = ToSchemaNode(child, false);
            if (node is SchemaGroup group)
            {
                members.Add(group);
            }
        }

        return schema;
    }

    public static XmlSchema FromSchema(Schema schema)
    {
        return new XmlSchema
        {
            Name = schema.Name,
            Members = schema.Members.Select(FromSchemaNode).ToList(),
            TypeDefs = schema.TypeDefs.Select(FromSchemaTypeDef).ToList(),
            ProcessorDefs = schema.ProcessorDefs.Select(FromSchemaProcessorDef).ToList(),
            Includes = schema.Includes.Select(include => new XmlSchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            Properties = schema.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static SchemaNode ToSchemaNode(XmlSchemaNode xmlNode, bool parentAllowsField)
    {
        return xmlNode switch
        {
            XmlSchemaField fieldNode when parentAllowsField => ToSchemaField(fieldNode),
            XmlSchemaField => throw new InvalidOperationException("Field nodes must have a group parent."),
            XmlSchemaRepeat repeatNode => ToSchemaRepeat(repeatNode),
            XmlSchemaChoice choiceNode => ToSchemaChoice(choiceNode),
            XmlSchemaGroup groupNode => ToSchemaGroup(groupNode),
            _ => throw new InvalidOperationException($"Unsupported XML schema node type '{xmlNode.GetType().Name}'.")
        };
    }

    private static SchemaField ToSchemaField(XmlSchemaField xmlField)
    {
        return new SchemaField
        {
            Name = xmlField.Name,
            ProcessorsList = xmlField.Processors.Select(ToSchemaProcessorRef).ToList(),
            DataType = new SchemaDataType(xmlField.Type ?? throw new InvalidOperationException($"The field '{xmlField.Name}' has no type.")),
            Value = ToSchemaValueOrText(xmlField.Value, xmlField.ValueRef),
            ByteLength = ToSchemaValue(xmlField.ByteLength, xmlField.ByteLengthRef),
            ByteOffset = xmlField.ByteOffset is { } offsetText ? Int32.Parse(offsetText, System.Globalization.CultureInfo.InvariantCulture) : null,
            PropertyList = MergeProperties(
                xmlField.Properties,
                xmlField.AdditionalAttributes,
                xmlField.AdditionalElements,
                FieldKnownNames)
        };
    }

    private static SchemaTypeDef ToSchemaTypeDef(XmlSchemaNode xmlNode)
    {
        if (xmlNode is XmlSchemaField fieldNode)
        {
            return new SchemaTypeDef
            {
                Name = fieldNode.Name,
                DataType = fieldNode.Type is { } typeName ? new SchemaDataType(typeName) : null,
                Processors = fieldNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    fieldNode.Properties,
                    fieldNode.AdditionalAttributes,
                    fieldNode.AdditionalElements,
                    FieldKnownNames)
            };
        }

        if (xmlNode is XmlSchemaRepeat repeatNode)
        {
            return new SchemaTypeDef
            {
                Name = repeatNode.Name,
                DataType = null,
                Processors = repeatNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    repeatNode.Properties,
                    repeatNode.AdditionalAttributes,
                    repeatNode.AdditionalElements,
                    RepeatKnownNames)
            };
        }

        if (xmlNode is XmlSchemaChoice choiceNode)
        {
            return new SchemaTypeDef
            {
                Name = choiceNode.Name,
                DataType = null,
                Processors = choiceNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    choiceNode.Properties,
                    choiceNode.AdditionalAttributes,
                    choiceNode.AdditionalElements,
                    ChoiceKnownNames)
            };
        }

        if (xmlNode is XmlSchemaGroup groupNode)
        {
            return new SchemaTypeDef
            {
                Name = groupNode.Name,
                DataType = null,
                Processors = groupNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    groupNode.Properties,
                    groupNode.AdditionalAttributes,
                    groupNode.AdditionalElements,
                    GroupKnownNames)
            };
        }

        throw new InvalidOperationException($"Unsupported XML schema typedef node type '{xmlNode.GetType().Name}'.");
    }

    private static SchemaRepeat ToSchemaGroup(XmlSchemaGroup xmlGroup)
    {
        var members = new List<SchemaNode>();

        var group = new SchemaRepeat
        {
            Name = xmlGroup.Name,
            ProcessorsList = xmlGroup.Processors.Select(ToSchemaProcessorRef).ToList(),
            MemberList = members,
            Count = 1,
            ByteSize = ToSchemaValue(xmlGroup.ByteSize, xmlGroup.ByteSizeRef),
            PropertyList = MergeProperties(
                xmlGroup.Properties,
                xmlGroup.AdditionalAttributes,
                xmlGroup.AdditionalElements,
                GroupKnownNames)
        };

        foreach (var child in xmlGroup.Members)
        {
            members.Add(ToSchemaNode(child, true));
        }

        return group;
    }

    private static SchemaRepeat ToSchemaRepeat(XmlSchemaRepeat xmlRepeat)
    {
        var members = new List<SchemaNode>();

        var repeat = new SchemaRepeat
        {
            Name = xmlRepeat.Name,
            ProcessorsList = xmlRepeat.Processors.Select(ToSchemaProcessorRef).ToList(),
            MemberList = members,
            Count = ToSchemaValue(xmlRepeat.Count, xmlRepeat.CountRef),
            ByteSize = ToSchemaValue(xmlRepeat.ByteSize, xmlRepeat.ByteSizeRef),
            ValueProcessorsList = xmlRepeat.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
            PropertyList = MergeProperties(
                xmlRepeat.Properties,
                xmlRepeat.AdditionalAttributes,
                xmlRepeat.AdditionalElements,
                RepeatKnownNames)
        };

        foreach (var child in xmlRepeat.Members)
        {
            members.Add(ToSchemaNode(child, true));
        }

        return repeat;
    }

    private static SchemaChoice ToSchemaChoice(XmlSchemaChoice xmlChoice)
    {
        var members = new List<SchemaNode>();

        var choice = new SchemaChoice
        {
            Name = xmlChoice.Name,
            ProcessorsList = xmlChoice.Processors.Select(ToSchemaProcessorRef).ToList(),
            MemberList = members,
            SelectedIndex = ToSchemaValue(xmlChoice.SelectedIndex, xmlChoice.SelectedIndexRef),
            ByteSize = ToSchemaValue(xmlChoice.ByteSize, xmlChoice.ByteSizeRef),
            ValueProcessorsList = xmlChoice.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
            PropertyList = MergeProperties(
                xmlChoice.Properties,
                xmlChoice.AdditionalAttributes,
                xmlChoice.AdditionalElements,
                ChoiceKnownNames)
        };

        foreach (var child in xmlChoice.Members)
        {
            members.Add(ToSchemaNode(child, true));
        }

        return choice;
    }

    private static SchemaProcessorDef ToSchemaProcessorDef(XmlSchemaProcessorDef processor)
    {
        return new SchemaProcessorDef
        {
            Name = processor.Name,
            Processor = new ProcessorKey(processor.Processor),
            PropertyList = MergeProperties(
                processor.Properties,
                processor.AdditionalAttributes,
                processor.AdditionalElements,
                ProcessorDefKnownNames)
        };
    }

    private static XmlSchemaProcessorDef FromSchemaProcessorDef(SchemaProcessorDef processor)
    {
        return new XmlSchemaProcessorDef
        {
            Name = processor.Name,
            Processor = processor.Processor.ToString(),
            Properties = processor.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static SchemaProcessorRef ToSchemaProcessorRef(XmlSchemaProcessorRef processor)
    {
        return new SchemaProcessorRef
        {
            Processor = new SchemaProcessorName(processor.Processor),
            PublishNamespace = processor.PubNs,
            PropertyList = MergeProperties(
                processor.Properties,
                processor.AdditionalAttributes,
                processor.AdditionalElements,
                ProcessorRefKnownNames)
        };
    }

    private static XmlSchemaNode FromSchemaNode(SchemaNode node)
    {
        if (node is SchemaField field)
        {
            return new XmlSchemaField
            {
                Name = field.Name,
                Processors = field.Processors.Select(FromSchemaProcessorRef).ToList(),
                Type = field.DataType.FullName,
                Value = ToConstantText(field.Value),
                ValueRef = ToXmlValueRef(field.Value),
                ByteLength = ToConstantText(field.ByteLength),
                ByteLengthRef = ToXmlValueRef(field.ByteLength),
                ByteOffset = field.ByteOffset?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (node is SchemaRepeat repeat)
        {
            return new XmlSchemaRepeat
            {
                Name = repeat.Name,
                Processors = repeat.Processors.Select(FromSchemaProcessorRef).ToList(),
                Members = repeat.Members.Select(FromSchemaNode).ToList(),
                Count = ToConstantText(repeat.Count),
                CountRef = ToXmlValueRef(repeat.Count),
                ByteSize = ToConstantText(repeat.ByteSize),
                ByteSizeRef = ToXmlValueRef(repeat.ByteSize),
                ValueProcessors = repeat.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = repeat.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (node is SchemaChoice choice)
        {
            return new XmlSchemaChoice
            {
                Name = choice.Name,
                Processors = choice.Processors.Select(FromSchemaProcessorRef).ToList(),
                Members = choice.Members.Select(FromSchemaNode).ToList(),
                SelectedIndex = ToConstantText(choice.SelectedIndex),
                SelectedIndexRef = ToXmlValueRef(choice.SelectedIndex),
                ByteSize = ToConstantText(choice.ByteSize),
                ByteSizeRef = ToXmlValueRef(choice.ByteSize),
                ValueProcessors = choice.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        throw new InvalidOperationException($"Unsupported schema node type '{node.GetType().Name}'.");
    }

    private static XmlSchemaNode FromSchemaTypeDef(SchemaTypeDef typeDef)
    {
        if (typeDef.DataType is { } typeDefType)
        {
            return new XmlSchemaField
            {
                Name = typeDef.Name,
                Type = typeDefType.FullName,
                Processors = typeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
                Properties = typeDef.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        return new XmlSchemaGroup
        {
            Name = typeDef.Name,
            Processors = typeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
            Members = [],
            Properties = typeDef.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static SchemaValueOrRef<int> ToSchemaValue(string? constant, XmlSchemaValueRef? valueRef)
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

        if (constant is null)
        {
            return default;
        }

        if (Int32.TryParse(constant, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new InvalidOperationException(
            $"'{constant}' is not a valid constant; use a nested element with a 'ref' attribute to refer to a value.");
    }

    private static XmlSchemaProcessorRef FromSchemaProcessorRef(SchemaProcessorRef processor)
    {
        return new XmlSchemaProcessorRef
        {
            Processor = processor.Processor.ToString(),
            PubNs = processor.PublishNamespace,
            Properties = processor.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static string? ToConstantText(SchemaValueOrRef<int> value)
        => value is int number ? number.ToString(CultureInfo.InvariantCulture) : null;

    private static string? ToConstantText(SchemaValueOrRef<string> value)
        => value is string text ? text : null;

    /// <summary>An unset value (optional) maps to the default (unset) value.</summary>
    private static SchemaValueOrRef<string> ToSchemaValueOrText(string? constant, XmlSchemaValueRef? valueRef)
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

        return constant is null ? default(SchemaValueOrRef<string>) : constant;
    }

    /// <summary>The 'ref' and 'pub' attributes are mutually exclusive; neither means unset (null).</summary>
    private static object? ParseValueRef(XmlSchemaValueRef? valueRef)
    {
        if (valueRef is null)
        {
            return null;
        }

        if (valueRef.Ref is not null && valueRef.Pub is not null)
        {
            throw new InvalidOperationException("A value reference cannot specify both 'ref' and 'pub'.");
        }

        if (valueRef.Ref is not null)
        {
            return SchemaNodeRef.TryParse(SchemaNodeRef.Prefix + valueRef.Ref, out var nodeRef)
                ? nodeRef
                : throw new InvalidOperationException($"Invalid node reference '{valueRef.Ref}'.");
        }

        if (valueRef.Pub is not null)
        {
            return SchemaPubRef.TryParse(SchemaPubRef.Prefix + valueRef.Pub, out var pubRef)
                ? pubRef
                : throw new InvalidOperationException($"Invalid published reference '{valueRef.Pub}'. Expected 'namespace.name'.");
        }

        return null;
    }

    private static XmlSchemaValueRef FromNodeRef(SchemaNodeRef nodeRef)
        => new() { Ref = nodeRef.ToString()[SchemaNodeRef.Prefix.Length..] };

    private static XmlSchemaValueRef FromPubRef(SchemaPubRef pubRef)
        => new() { Pub = pubRef.ToString()[SchemaPubRef.Prefix.Length..] };

    private static XmlSchemaValueRef? ToXmlValueRef(SchemaValueOrRef<string> value)
        => value switch
        {
            SchemaNodeRef nodeRef => FromNodeRef(nodeRef),
            SchemaPubRef pubRef => FromPubRef(pubRef),
            _ => null
        };

    private static XmlSchemaValueRef? ToXmlValueRef(SchemaValueOrRef<int> value)
        => value switch
        {
            SchemaNodeRef nodeRef => FromNodeRef(nodeRef),
            SchemaPubRef pubRef => FromPubRef(pubRef),
            _ => null
        };

    private static XmlSchemaProperty FromSchemaProperty(SchemaProperty property)
    {
        return new XmlSchemaProperty
        {
            Name = property.Name,
            Value = property.Value,
            Nil = property.Value is null
        };
    }

    private static List<SchemaProperty> MergeProperties(
        IReadOnlyList<XmlSchemaProperty> explicitProperties,
        XmlAttribute[]? additionalAttributes,
        XmlElement[]? additionalElements,
        IReadOnlySet<string> knownNames)
    {
        var merged = explicitProperties.Select(ToSchemaProperty).ToList();

        if (additionalAttributes is not null)
        {
            foreach (var attribute in additionalAttributes)
            {
                if (knownNames.Contains(attribute.LocalName))
                {
                    continue;
                }

                merged.Add(new SchemaProperty
                {
                    Name = attribute.LocalName,
                    Value = attribute.Value
                });
            }
        }

        if (additionalElements is not null)
        {
            foreach (var element in additionalElements)
            {
                if (knownNames.Contains(element.LocalName))
                {
                    continue;
                }

                var value = element.GetAttribute("nil", "http://www.w3.org/2001/XMLSchema-instance") == "true"
                    ? null
                    : element.HasChildNodes && element.ChildNodes.OfType<XmlElement>().Any()
                        ? element.OuterXml
                        : element.InnerText;

                merged.Add(new SchemaProperty
                {
                    Name = element.LocalName,
                    Value = value
                });
            }
        }

        return merged;
    }

    private static SchemaProperty ToSchemaProperty(XmlSchemaProperty xmlProperty)
    {
        var value = xmlProperty.Nil ? null : xmlProperty.Value ?? xmlProperty.Text ?? string.Empty;

        return new SchemaProperty
        {
            Name = xmlProperty.Name,
            Value = value
        };
    }
}
