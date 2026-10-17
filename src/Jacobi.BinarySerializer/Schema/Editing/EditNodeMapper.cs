using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema.Editing;

public static class EditNodeMapper
{
    public static EditNode ToEditNode(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        EditNode edit = node switch
        {
            SchemaDocument document => ToEditDocument(document),
            SchemaNodeDef definition => new EditNodeDef { Kind = definition.Kind, DataType = definition.DataType },
            SchemaField field => new EditField
            {
                DataType = field.DataType, Value = Copy(field.Value),
                ByteLength = Copy(field.ByteLength), ByteOffset = field.ByteOffset
            },
            SchemaPackedGroup packed => new EditPackedGroup { ByteLength = Copy(packed.ByteLength) },
            SchemaRepeat repeat => new EditRepeat { Count = Copy(repeat.Count) },
            SchemaChoice choice => new EditChoice
            {
                SelectedIndex = Copy(choice.SelectedIndex), MemberNodeDef = choice.MemberNodeDef, Pipeline = choice.Pipeline
            },
            SchemaGroup when node.GetType() == typeof(SchemaGroup) => new EditGroup(),
            _ => throw new NotSupportedException($"Unsupported schema node type: {node.GetType().Name}.")
        };
        if (node is SchemaDocument)
        {
            return edit;
        }
        edit.Name = node.Name;
        edit.NodeDef = node.NodeDef;
        CopyProperties(node.Properties, edit.Properties);
        switch (node, edit)
        {
            case (SchemaField field, EditField target):
                CopyProcessors(field.Processors, target.Processors);
                break;
            case (SchemaGroup group, EditGroup target):
                ReadGroup(group, target);
                break;
            case (SchemaNodeDef definition, EditNodeDef target):
                target.Members.AddRange(definition.Members.Select(ToEditNode));
                CopyProcessors(definition.Processors, target.Processors);
                break;
        }
        if (node is SchemaRepeat sourceRepeat && edit is EditRepeat editRepeat)
        {
            CopyProcessors(sourceRepeat.ValueProcessors, editRepeat.ValueProcessors);
        }
        if (node is SchemaChoice sourceChoice && edit is EditChoice editChoice)
        {
            CopyProcessors(sourceChoice.ValueProcessors, editChoice.ValueProcessors);
        }
        return edit;
    }

    public static EditDocument ToEditDocument(SchemaDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsCompiled)
        {
            throw new ArgumentException("Edit the authored document before compilation, not an expanded runtime schema.", nameof(document));
        }
        var edit = new EditDocument { Name = document.Name, NodeDef = document.NodeDef };
        CopyProperties(document.Properties, edit.Properties);
        ReadGroup(document, edit);
        edit.NodeDefs.AddRange(document.NodeDefs.Select(definition => (EditNodeDef)ToEditNode(definition)));
        foreach (var definition in document.DataTypeDefs)
        {
            var target = new EditDataTypeDef
            {
                Name = definition.Name, BasedOn = definition.BasedOn,
                Min = definition.Min, Max = definition.Max, Scale = definition.Scale, Shift = definition.Shift,
                Options = definition.Options?.ToDictionary(pair => pair.Key, pair => pair.Value)
            };
            CopyProperties(definition.Properties, target.Properties);
            CopyProcessors(definition.Processors, target.Processors);
            edit.DataTypeDefs.Add(target);
        }
        foreach (var definition in document.ProcessorDefs)
        {
            var target = new EditProcessorDef { Name = definition.Name, Processor = definition.Processor.ToString() };
            CopyProperties(definition.Properties, target.Properties);
            edit.ProcessorDefs.Add(target);
        }
        edit.Includes.AddRange(document.Includes.Select(include => new EditDocumentRef { Schema = include.Schema, Path = include.Path }));
        return edit;
    }

    public static SchemaNode ToSchemaNode(EditNode edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        SchemaNode node = edit switch
        {
            EditDocument document => ToSchemaDocument(document),
            EditNodeDef definition => new SchemaNodeDef
            {
                Name = edit.Name, NodeDef = edit.NodeDef, Kind = definition.Kind, DataType = definition.DataType,
                MemberList = definition.Members.Select(ToSchemaNode).ToList(), Processors = WriteProcessors(definition.Processors)
            },
            EditField field => new SchemaField
            {
                Name = edit.Name, NodeDef = edit.NodeDef, DataType = field.DataType,
                Value = Copy(field.Value), ByteLength = Copy(field.ByteLength), ByteOffset = field.ByteOffset,
                ProcessorsList = WriteProcessors(field.Processors)
            },
            EditPackedGroup packed => new SchemaPackedGroup
            {
                Name = edit.Name, NodeDef = edit.NodeDef, ByteLength = Copy(packed.ByteLength), ByteSize = Copy(packed.ByteSize)
            },
            EditRepeat repeat => new SchemaRepeat
            {
                Name = edit.Name, NodeDef = edit.NodeDef, Count = Copy(repeat.Count), ByteSize = Copy(repeat.ByteSize),
                ValueProcessorsList = WriteProcessors(repeat.ValueProcessors)
            },
            EditChoice choice => new SchemaChoice
            {
                Name = edit.Name, NodeDef = edit.NodeDef, SelectedIndex = Copy(choice.SelectedIndex),
                MemberNodeDef = choice.MemberNodeDef, Pipeline = choice.Pipeline, ByteSize = Copy(choice.ByteSize),
                ValueProcessorsList = WriteProcessors(choice.ValueProcessors)
            },
            EditGroup plainGroup when edit.GetType() == typeof(EditGroup) => new SchemaGroup
            {
                Name = edit.Name, NodeDef = edit.NodeDef, ByteSize = Copy(plainGroup.ByteSize)
            },
            _ => throw new NotSupportedException($"Unsupported edit node type: {edit.GetType().Name}.")
        };
        if (edit is EditDocument)
        {
            return node;
        }
        node.PropertyList.AddRange(WriteProperties(edit.Properties));
        if (edit is EditGroup group && node is SchemaGroup target)
        {
            return WriteGroup(group, target);
        }
        return node;
    }

    public static SchemaDocument ToSchemaDocument(EditDocument edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var schema = new SchemaDocument
        {
            Name = edit.Name, NodeDef = edit.NodeDef, ByteSize = Copy(edit.ByteSize),
            Roots = [], Groups = [], Fields = [],
            PropertyList = WriteProperties(edit.Properties),
            NodeDefs = edit.NodeDefs.Select(definition => (SchemaNodeDef)ToSchemaNode(definition)).ToList(),
            DataTypeDefs = edit.DataTypeDefs.Select(definition => new SchemaDataTypeDef
            {
                Name = definition.Name, BasedOn = definition.BasedOn,
                Min = definition.Min, Max = definition.Max, Scale = definition.Scale, Shift = definition.Shift,
                Options = definition.Options?.ToDictionary(pair => pair.Key, pair => pair.Value),
                Processors = WriteProcessors(definition.Processors), PropertyList = WriteProperties(definition.Properties)
            }).ToList(),
            ProcessorDefs = edit.ProcessorDefs.Select(definition => new SchemaProcessorDef
            {
                Name = definition.Name, Processor = new ProcessorKey(definition.Processor),
                PropertyList = WriteProperties(definition.Properties)
            }).ToList(),
            Includes = edit.Includes.Select(include => new SchemaDocumentRef { Schema = include.Schema, Path = include.Path }).ToList()
        };
        WriteGroup(edit, schema);
        schema.RefreshIndexes();
        return new SchemaDocument
        {
            Name = schema.Name, NodeDef = schema.NodeDef, ByteSize = schema.ByteSize,
            MemberList = schema.MemberList, PropertyList = schema.PropertyList, ProcessorsList = schema.ProcessorsList,
            NodeDefs = schema.NodeDefs, DataTypeDefs = schema.DataTypeDefs,
            ProcessorDefs = schema.ProcessorDefs, Includes = schema.Includes,
            Roots = schema.Members.OfType<SchemaGroup>().ToArray(), Groups = schema.Groups, Fields = schema.Fields
        };
    }

    private static void ReadGroup(SchemaGroup source, EditGroup target)
    {
        target.ByteSize = Copy(source.ByteSize);
        target.Members.AddRange(source.Members.Select(ToEditNode));
        CopyProcessors(source.Processors, target.Processors);
    }

    private static TGroup WriteGroup<TGroup>(EditGroup source, TGroup target) where TGroup : SchemaGroup
    {
        target.MemberList.AddRange(source.Members.Select(ToSchemaNode));
        target.ProcessorsList.AddRange(WriteProcessors(source.Processors));
        return target;
    }

    private static void CopyProperties(IEnumerable<SchemaProperty> source, List<EditProperty> target)
        => target.AddRange(source.Select(property => new EditProperty { Name = property.Name, Value = property.Value }));

    private static List<SchemaProperty> WriteProperties(IEnumerable<EditProperty> source)
        => source.Select(property => new SchemaProperty { Name = property.Name, Value = property.Value }).ToList();

    private static void CopyProcessors(IEnumerable<SchemaProcessorRef> source, List<EditProcessorRef> target)
    {
        foreach (var processor in source)
        {
            var edit = new EditProcessorRef { Processor = processor.Processor, PublishNamespace = processor.PublishNamespace };
            CopyProperties(processor.Properties, edit.Properties);
            target.Add(edit);
        }
    }

    private static List<SchemaProcessorRef> WriteProcessors(IEnumerable<EditProcessorRef> source)
        => source.Select(processor => new SchemaProcessorRef
        {
            Processor = processor.Processor, PublishNamespace = processor.PublishNamespace,
            PropertyList = WriteProperties(processor.Properties)
        }).ToList();

    private static SchemaValueOrRef<T> Copy<T>(SchemaValueOrRef<T> value) => value switch
    {
        SchemaNodeRef reference => new SchemaNodeRef { Path = reference.Path, Indices = reference.Indices.ToArray() },
        SchemaPubRef reference => new SchemaPubRef { Namespace = reference.Namespace, Name = reference.Name },
        T constant => constant,
        _ => default
    };
}