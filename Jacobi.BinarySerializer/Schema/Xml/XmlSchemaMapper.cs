using System.Globalization;
using System.Xml;

namespace Jacobi.BinarySerializer.Schema.Xml;

internal static class XmlSchemaMapper
{
    private static readonly HashSet<string> SchemaKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchema.Name),
        nameof(XmlSchema.Children),
        nameof(XmlSchema.TypeDefs),
        nameof(XmlSchema.CodecDefs),
        nameof(XmlSchema.Includes),
        nameof(XmlSchema.Properties)
    };

    private static readonly HashSet<string> GroupKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaGroupNode.Name),
        nameof(XmlSchemaGroupNode.Pipeline),
        nameof(XmlSchemaGroupNode.Children),
        nameof(XmlSchemaGroupNode.Properties)
    };

    private static readonly HashSet<string> RepeatKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaRepeatNode.Name),
        nameof(XmlSchemaRepeatNode.Pipeline),
        nameof(XmlSchemaRepeatNode.Children),
        nameof(XmlSchemaRepeatNode.Count),
        nameof(XmlSchemaRepeatNode.Properties)
    };

    private static readonly HashSet<string> ChoiceKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaChoiceNode.Name),
        nameof(XmlSchemaChoiceNode.Pipeline),
        nameof(XmlSchemaChoiceNode.Children),
        nameof(XmlSchemaChoiceNode.SelectedIndex),
        nameof(XmlSchemaChoiceNode.Properties)
    };

    private static readonly HashSet<string> FieldKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaFieldNode.Name),
        nameof(XmlSchemaFieldNode.Codec),
        nameof(XmlSchemaFieldNode.Type),
        nameof(XmlSchemaFieldNode.Properties)
    };

    private static readonly HashSet<string> CodecRefKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaCodecRef.Codec),
        nameof(XmlSchemaCodecRef.Properties)
    };

    public static Schema ToSchema(XmlSchema xmlSchema)
    {
        var children = new List<SchemaGroup>();

        var schema = new Schema
        {
            Name = xmlSchema.Name,
            Children = children,
            TypeDefs = xmlSchema.TypeDefs.Select(typeDef => ToSchemaNode(typeDef, true)).ToList(),
            CodecDefs = xmlSchema.CodecDefs.Select(ToSchemaCodecRef).ToList(),
            Includes = xmlSchema.Includes.Select(include => new SchemaDocumentRef
            {
                Schema = include.Schema,
                Path = include.Path
            }).ToList(),
            Properties = MergeProperties(
                xmlSchema.Properties,
                xmlSchema.AdditionalAttributes,
                xmlSchema.AdditionalElements,
                SchemaKnownNames)
        };

        foreach (var child in xmlSchema.Children)
        {
            var node = ToSchemaNode(child, false);
            if (node is SchemaGroup group)
            {
                children.Add(group);
            }
        }

        return schema;
    }

    public static XmlSchema FromSchema(Schema schema)
    {
        return new XmlSchema
        {
            Name = schema.Name,
            Children = schema.Children.Select(FromSchemaNode).ToList(),
            TypeDefs = schema.TypeDefs.Select(FromSchemaNode).ToList(),
            CodecDefs = schema.CodecDefs.Select(FromSchemaCodecRef).ToList(),
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
            XmlSchemaFieldNode fieldNode when parentAllowsField => ToSchemaField(fieldNode),
            XmlSchemaFieldNode => throw new InvalidOperationException("Field nodes must have a group parent."),
            XmlSchemaRepeatNode repeatNode => ToSchemaRepeat(repeatNode),
            XmlSchemaChoiceNode choiceNode => ToSchemaChoice(choiceNode),
            XmlSchemaGroupNode groupNode => ToSchemaGroup(groupNode),
            _ => throw new InvalidOperationException($"Unsupported XML schema node type '{xmlNode.GetType().Name}'.")
        };
    }

    private static SchemaField ToSchemaField(XmlSchemaFieldNode xmlField)
    {
        return new SchemaField
        {
            Name = xmlField.Name,
            Codec = ToSchemaCodecRef(xmlField.Codec),
            Type = xmlField.Type,
            Properties = MergeProperties(
                xmlField.Properties,
                xmlField.AdditionalAttributes,
                xmlField.AdditionalElements,
                FieldKnownNames)
        };
    }

    private static SchemaGroup ToSchemaGroup(XmlSchemaGroupNode xmlGroup)
    {
        var children = new List<SchemaNode>();

        var group = new SchemaGroup
        {
            Name = xmlGroup.Name,
            Pipeline = xmlGroup.Pipeline.Select(ToSchemaCodecRef).ToList(),
            Children = children,
            Properties = MergeProperties(
                xmlGroup.Properties,
                xmlGroup.AdditionalAttributes,
                xmlGroup.AdditionalElements,
                GroupKnownNames)
        };

        foreach (var child in xmlGroup.Children)
        {
            children.Add(ToSchemaNode(child, true));
        }

        return group;
    }

    private static SchemaRepeat ToSchemaRepeat(XmlSchemaRepeatNode xmlRepeat)
    {
        var children = new List<SchemaNode>();

        var repeat = new SchemaRepeat
        {
            Name = xmlRepeat.Name,
            Pipeline = xmlRepeat.Pipeline.Select(ToSchemaCodecRef).ToList(),
            Children = children,
            Count = ParseCodecOrInt32(xmlRepeat.Count),
            Properties = MergeProperties(
                xmlRepeat.Properties,
                xmlRepeat.AdditionalAttributes,
                xmlRepeat.AdditionalElements,
                RepeatKnownNames)
        };

        foreach (var child in xmlRepeat.Children)
        {
            children.Add(ToSchemaNode(child, true));
        }

        return repeat;
    }

    private static SchemaChoice ToSchemaChoice(XmlSchemaChoiceNode xmlChoice)
    {
        var children = new List<SchemaNode>();

        var choice = new SchemaChoice
        {
            Name = xmlChoice.Name,
            Pipeline = xmlChoice.Pipeline.Select(ToSchemaCodecRef).ToList(),
            Children = children,
            SelectedIndex = ParseCodecOrInt32(xmlChoice.SelectedIndex),
            Properties = MergeProperties(
                xmlChoice.Properties,
                xmlChoice.AdditionalAttributes,
                xmlChoice.AdditionalElements,
                ChoiceKnownNames)
        };

        foreach (var child in xmlChoice.Children)
        {
            children.Add(ToSchemaNode(child, true));
        }

        return choice;
    }

    private static SchemaCodecRef ToSchemaCodecRef(XmlSchemaCodecRef codec)
    {
        return new SchemaCodecRef
        {
            Codec = codec.Codec,
            Properties = MergeProperties(
                codec.Properties,
                null,
                null,
                CodecRefKnownNames)
        };
    }

    private static XmlSchemaNode FromSchemaNode(SchemaNode node)
    {
        return node switch
        {
            SchemaField field => new XmlSchemaFieldNode
            {
                Name = field.Name,
                Codec = FromSchemaCodecRef(field.Codec),
                Type = field.Type,
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaRepeat repeat => new XmlSchemaRepeatNode
            {
                Name = repeat.Name,
                Pipeline = repeat.Pipeline.Select(FromSchemaCodecRef).ToList(),
                Children = repeat.Children.Select(FromSchemaNode).ToList(),
                Count = ToStringValue(repeat.Count),
                Properties = repeat.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaChoice choice => new XmlSchemaChoiceNode
            {
                Name = choice.Name,
                Pipeline = choice.Pipeline.Select(FromSchemaCodecRef).ToList(),
                Children = choice.Children.Select(FromSchemaNode).ToList(),
                SelectedIndex = ToStringValue(choice.SelectedIndex),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            },
            SchemaGroup group => new XmlSchemaGroupNode
            {
                Name = group.Name,
                Pipeline = group.Pipeline.Select(FromSchemaCodecRef).ToList(),
                Children = group.Children.Select(FromSchemaNode).ToList(),
                Properties = group.Properties.Select(FromSchemaProperty).ToList()
            },
            _ => throw new InvalidOperationException($"Unsupported schema node type '{node.GetType().Name}'.")
        };
    }

    private static SchemaCodecOrValue<int> ParseCodecOrInt32(string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return new SchemaCodecRef
        {
            Codec = value ?? string.Empty,
            Properties = []
        };
    }

    private static XmlSchemaCodecRef FromSchemaCodecRef(SchemaCodecRef codec)
    {
        return new XmlSchemaCodecRef
        {
            Codec = codec.Codec,
            Properties = codec.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static string ToStringValue(SchemaCodecOrValue<int> value)
    {
        return value switch
        {
            int number => number.ToString(CultureInfo.InvariantCulture),
            SchemaCodecRef codec => codec.Codec,
            _ => string.Empty
        };
    }

    private static XmlSchemaProperty FromSchemaProperty(SchemaProperty property)
    {
        return new XmlSchemaProperty
        {
            Name = property.Name,
            Value = property.Value
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

                var value = element.HasChildNodes && element.ChildNodes.OfType<XmlElement>().Any()
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
        var value = xmlProperty.Value ?? xmlProperty.Text ?? string.Empty;

        return new SchemaProperty
        {
            Name = xmlProperty.Name,
            Value = value
        };
    }
}
