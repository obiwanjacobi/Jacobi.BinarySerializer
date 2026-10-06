using System.Globalization;
using System.Xml;
using Jacobi.BinarySerializer.Processor;

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
        nameof(XmlSchemaGroup.Name),
        nameof(XmlSchemaGroup.Processors),
        nameof(XmlSchemaGroup.Children),
        nameof(XmlSchemaGroup.Size),
        nameof(XmlSchemaGroup.SizeRef),
        nameof(XmlSchemaGroup.Properties)
    };

    private static readonly HashSet<string> RepeatKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaRepeat.Name),
        nameof(XmlSchemaRepeat.Processors),
        nameof(XmlSchemaRepeat.Children),
        nameof(XmlSchemaRepeat.Count),
        nameof(XmlSchemaRepeat.CountRef),
        nameof(XmlSchemaRepeat.Size),
        nameof(XmlSchemaRepeat.SizeRef),
        nameof(XmlSchemaRepeat.ValueProcessors),
        nameof(XmlSchemaRepeat.Properties)
    };

    private static readonly HashSet<string> ChoiceKnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(XmlSchemaChoice.Name),
        nameof(XmlSchemaChoice.Processors),
        nameof(XmlSchemaChoice.Children),
        nameof(XmlSchemaChoice.SelectedIndex),
        nameof(XmlSchemaChoice.SelectedIndexRef),
        nameof(XmlSchemaChoice.Size),
        nameof(XmlSchemaChoice.SizeRef),
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
        nameof(XmlSchemaField.Length),
        nameof(XmlSchemaField.LengthRef),
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
            DataType = xmlField.Type,
            Value = ToSchemaValueOrText(xmlField.Value, xmlField.ValueRef),
            Length = ToSchemaValue(xmlField.Length, xmlField.LengthRef),
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
                DataType = fieldNode.Type,
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
                DataType = SchemaDataType.None,
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
                DataType = SchemaDataType.None,
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
                DataType = SchemaDataType.None,
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
        var children = new List<SchemaNode>();

        var group = new SchemaRepeat
        {
            Name = xmlGroup.Name,
            ProcessorsList = xmlGroup.Processors.Select(ToSchemaProcessorRef).ToList(),
            ChildList = children,
            Count = 1,
            Size = ToSchemaValue(xmlGroup.Size, xmlGroup.SizeRef),
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

    private static SchemaRepeat ToSchemaRepeat(XmlSchemaRepeat xmlRepeat)
    {
        var children = new List<SchemaNode>();

        var repeat = new SchemaRepeat
        {
            Name = xmlRepeat.Name,
            ProcessorsList = xmlRepeat.Processors.Select(ToSchemaProcessorRef).ToList(),
            ChildList = children,
            Count = ToSchemaValue(xmlRepeat.Count, xmlRepeat.CountRef),
            Size = ToSchemaValue(xmlRepeat.Size, xmlRepeat.SizeRef),
            ValueProcessorsList = xmlRepeat.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
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

    private static SchemaChoice ToSchemaChoice(XmlSchemaChoice xmlChoice)
    {
        var children = new List<SchemaNode>();

        var choice = new SchemaChoice
        {
            Name = xmlChoice.Name,
            ProcessorsList = xmlChoice.Processors.Select(ToSchemaProcessorRef).ToList(),
            ChildList = children,
            SelectedIndex = ToSchemaValue(xmlChoice.SelectedIndex, xmlChoice.SelectedIndexRef),
            Size = ToSchemaValue(xmlChoice.Size, xmlChoice.SizeRef),
            ValueProcessorsList = xmlChoice.ValueProcessors.Select(ToSchemaProcessorRef).ToList(),
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

    private static SchemaProcessorDef ToSchemaProcessorDef(XmlSchemaProcessorDef processor)
    {
        return new SchemaProcessorDef
        {
            Name = processor.Name,
            Processor = new ProcessorKey(processor.Processor),
            PropertyList = MergeProperties(
                processor.Properties,
                null,
                null,
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
            return new XmlSchemaField
            {
                Name = field.Name,
                Processors = field.Processors.Select(FromSchemaProcessorRef).ToList(),
                Type = field.DataType,
                Value = ToConstantText(field.Value),
                ValueRef = ToXmlValueRef(field.Value),
                Length = ToConstantText(field.Length),
                LengthRef = ToXmlValueRef(field.Length),
                Properties = field.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        if (node is SchemaRepeat repeat)
        {
            return new XmlSchemaRepeat
            {
                Name = repeat.Name,
                Processors = repeat.Processors.Select(FromSchemaProcessorRef).ToList(),
                Children = repeat.Children.Select(FromSchemaNode).ToList(),
                Count = ToConstantText(repeat.Count),
                CountRef = ToXmlValueRef(repeat.Count),
                Size = ToConstantText(repeat.Size),
                SizeRef = ToXmlValueRef(repeat.Size),
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
                Children = choice.Children.Select(FromSchemaNode).ToList(),
                SelectedIndex = ToConstantText(choice.SelectedIndex),
                SelectedIndexRef = ToXmlValueRef(choice.SelectedIndex),
                Size = ToConstantText(choice.Size),
                SizeRef = ToXmlValueRef(choice.Size),
                ValueProcessors = choice.ValueProcessors.Select(FromSchemaProcessorRef).ToList(),
                Properties = choice.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        throw new InvalidOperationException($"Unsupported schema node type '{node.GetType().Name}'.");
    }

    private static XmlSchemaNode FromSchemaTypeDef(SchemaTypeDef typeDef)
    {
        if (typeDef.DataType != SchemaDataType.None)
        {
            return new XmlSchemaField
            {
                Name = typeDef.Name,
                Type = typeDef.DataType,
                Processors = typeDef.Processors.Select(FromSchemaProcessorRef).ToList(),
                Properties = typeDef.Properties.Select(FromSchemaProperty).ToList()
            };
        }

        return new XmlSchemaGroup
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
            if (SchemaNodeRef.TryParse(valueRef.Reference, out var nodeRef))
            {
                return nodeRef;
            }

            if (SchemaPubRef.TryParse(valueRef.Reference, out var pubRef))
            {
                return pubRef;
            }

            throw new InvalidOperationException(
                $"Invalid value reference '{valueRef.Reference}'. Expected 'ref:path' or 'pub:namespace.name'.");
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
        if (valueRef is not null)
        {
            if (SchemaNodeRef.TryParse(valueRef.Reference, out var nodeRef))
            {
                return nodeRef;
            }

            if (SchemaPubRef.TryParse(valueRef.Reference, out var pubRef))
            {
                return pubRef;
            }

            throw new InvalidOperationException(
                $"Invalid value reference '{valueRef.Reference}'. Expected 'ref:path' or 'pub:namespace.name'.");
        }

        return constant is null ? default(SchemaValueOrRef<string>) : constant;
    }

    private static XmlSchemaValueRef? ToXmlValueRef(SchemaValueOrRef<string> value)
        => value switch
        {
            SchemaNodeRef nodeRef => new XmlSchemaValueRef { Reference = nodeRef.ToString() },
            SchemaPubRef pubRef => new XmlSchemaValueRef { Reference = pubRef.ToString() },
            _ => null
        };

    private static XmlSchemaValueRef? ToXmlValueRef(SchemaValueOrRef<int> value)
        => value switch
        {
            SchemaNodeRef nodeRef => new XmlSchemaValueRef { Reference = nodeRef.ToString() },
            SchemaPubRef pubRef => new XmlSchemaValueRef { Reference = pubRef.ToString() },
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
