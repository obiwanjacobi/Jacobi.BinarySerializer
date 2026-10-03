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
        nameof(XmlSchema.ProcessorDefs),
        nameof(XmlSchema.Includes),
        nameof(XmlSchema.Properties)
    };

    private static readonly HashSet<string> GroupKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaGroupNode.Name),
        nameof(XmlSchemaGroupNode.Processors),
        nameof(XmlSchemaGroupNode.Children),
        nameof(XmlSchemaGroupNode.Properties)
    };

    private static readonly HashSet<string> RepeatKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaRepeatNode.Name),
        nameof(XmlSchemaRepeatNode.Processors),
        nameof(XmlSchemaRepeatNode.Children),
        nameof(XmlSchemaRepeatNode.Count),
        nameof(XmlSchemaRepeatNode.CountRef),
        nameof(XmlSchemaRepeatNode.Properties)
    };

    private static readonly HashSet<string> ChoiceKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaChoiceNode.Name),
        nameof(XmlSchemaChoiceNode.Processors),
        nameof(XmlSchemaChoiceNode.Children),
        nameof(XmlSchemaChoiceNode.SelectedIndex),
        nameof(XmlSchemaChoiceNode.SelectedIndexRef),
        nameof(XmlSchemaChoiceNode.Properties)
    };

    private static readonly HashSet<string> FieldKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaFieldNode.Name),
        nameof(XmlSchemaFieldNode.Processors),
        nameof(XmlSchemaFieldNode.Type),
        nameof(XmlSchemaFieldNode.Properties)
    };

    private static readonly HashSet<string> ProcessorRefKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaProcessorRef.Processor),
        nameof(XmlSchemaProcessorRef.Properties)
    };

    public static Schema ToSchema(XmlSchema xmlSchema)
    {
        var children = new List<SchemaNode>();

        var schema = new Schema
        {
            Name = xmlSchema.Name,
            ChildList = children,
            TypeDefs = xmlSchema.TypeDefs.Select(ToSchemaTypeDef).ToList(),
            ProcessorDefs = xmlSchema.ProcessorDefs.Select(ToSchemaProcessorRef).ToList(),
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
            TypeDefs = schema.TypeDefs.Select(FromSchemaTypeDef).ToList(),
            ProcessorDefs = schema.ProcessorDefs.Select(FromSchemaProcessorRef).ToList(),
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
            ProcessorsList = xmlField.Processors.Select(ToSchemaProcessorRef).ToList(),
            Type = xmlField.Type,
            PropertyList = MergeProperties(
                xmlField.Properties,
                xmlField.AdditionalAttributes,
                xmlField.AdditionalElements,
                FieldKnownNames)
        };
    }

    private static SchemaTypeDef ToSchemaTypeDef(XmlSchemaNode xmlNode)
    {
        if (xmlNode is XmlSchemaFieldNode fieldNode)
        {
            return new SchemaTypeDef
            {
                Name = fieldNode.Name,
                Type = fieldNode.Type,
                Processors = fieldNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    fieldNode.Properties,
                    fieldNode.AdditionalAttributes,
                    fieldNode.AdditionalElements,
                    FieldKnownNames)
            };
        }

        if (xmlNode is XmlSchemaRepeatNode repeatNode)
        {
            return new SchemaTypeDef
            {
                Name = repeatNode.Name,
                Type = SchemaDataType.None,
                Processors = repeatNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    repeatNode.Properties,
                    repeatNode.AdditionalAttributes,
                    repeatNode.AdditionalElements,
                    RepeatKnownNames)
            };
        }

        if (xmlNode is XmlSchemaChoiceNode choiceNode)
        {
            return new SchemaTypeDef
            {
                Name = choiceNode.Name,
                Type = SchemaDataType.None,
                Processors = choiceNode.Processors.Select(ToSchemaProcessorRef).ToList(),
                PropertyList = MergeProperties(
                    choiceNode.Properties,
                    choiceNode.AdditionalAttributes,
                    choiceNode.AdditionalElements,
                    ChoiceKnownNames)
            };
        }

        if (xmlNode is XmlSchemaGroupNode groupNode)
        {
            return new SchemaTypeDef
            {
                Name = groupNode.Name,
                Type = SchemaDataType.None,
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

    private static SchemaRepeat ToSchemaGroup(XmlSchemaGroupNode xmlGroup)
    {
        var children = new List<SchemaNode>();

        var group = new SchemaRepeat
        {
            Name = xmlGroup.Name,
            ProcessorsList = xmlGroup.Processors.Select(ToSchemaProcessorRef).ToList(),
            ChildList = children,
            Count = 1,
            PropertyList = MergeProperties(
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
            ProcessorsList = xmlRepeat.Processors.Select(ToSchemaProcessorRef).ToList(),
            ChildList = children,
            Count = ToSchemaValue(xmlRepeat.Count, xmlRepeat.CountRef),
            PropertyList = MergeProperties(
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
            ProcessorsList = xmlChoice.Processors.Select(ToSchemaProcessorRef).ToList(),
            ChildList = children,
            SelectedIndex = ToSchemaValue(xmlChoice.SelectedIndex, xmlChoice.SelectedIndexRef),
            PropertyList = MergeProperties(
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

    private static SchemaProcessorRef ToSchemaProcessorRef(XmlSchemaProcessorRef processor)
    {
        return new SchemaProcessorRef
        {
            Processor = new SchemaName(processor.Processor),
            PropertyList = MergeProperties(
                processor.Properties,
                null,
                null,
                ProcessorRefKnownNames)
        };
    }

    private static XmlSchemaNode FromSchemaNode(SchemaNode node)
    {
        if (node is SchemaField field)
        {
            return new XmlSchemaFieldNode
            {
                Name = field.Name,
                Processors = field.Processors.Select(FromSchemaProcessorRef).ToList(),
                Type = field.Type,
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (node is SchemaRepeat repeat)
        {
            return new XmlSchemaRepeatNode
            {
                Name = repeat.Name,
                Processors = repeat.Processors.Select(FromSchemaProcessorRef).ToList(),
                Children = repeat.Children.Select(FromSchemaNode).ToList(),
                Count = ToConstantText(repeat.Count),
                CountRef = ToXmlValueRef(repeat.Count),
                Properties = repeat.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (node is SchemaChoice choice)
        {
            return new XmlSchemaChoiceNode
            {
                Name = choice.Name,
                Processors = choice.Processors.Select(FromSchemaProcessorRef).ToList(),
                Children = choice.Children.Select(FromSchemaNode).ToList(),
                SelectedIndex = ToConstantText(choice.SelectedIndex),
                SelectedIndexRef = ToXmlValueRef(choice.SelectedIndex),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        throw new InvalidOperationException($"Unsupported schema node type '{node.GetType().Name}'.");
    }

    private static XmlSchemaNode FromSchemaTypeDef(SchemaTypeDef typeDef)
    {
        if (typeDef.Type != SchemaDataType.None)
        {
            return new XmlSchemaFieldNode
            {
                Name = typeDef.Name,
                Type = typeDef.Type,
                Processors = typeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
                Properties = typeDef.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        return new XmlSchemaGroupNode
        {
            Name = typeDef.Name,
            Processors = typeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
            Children = [],
            Properties = typeDef.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static SchemaValueOrRef<int> ToSchemaValue(string? constant, XmlSchemaValueRef? valueRef)
    {
        if (valueRef is not null)
        {
            return new SchemaValueRef { Reference = valueRef.Reference };
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
            Properties = processor.Properties.Select(FromSchemaProperty).ToList()
        };
    }

    private static string? ToConstantText(SchemaValueOrRef<int> value)
        => value is int number ? number.ToString(CultureInfo.InvariantCulture) : null;

    private static XmlSchemaValueRef? ToXmlValueRef(SchemaValueOrRef<int> value)
        => value is SchemaValueRef valueRef ? new XmlSchemaValueRef { Reference = valueRef.Reference } : null;

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
