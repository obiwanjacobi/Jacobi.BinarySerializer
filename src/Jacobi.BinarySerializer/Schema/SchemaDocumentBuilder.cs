using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Schema;

/// <summary>
/// Fluent builder that dynamically creates a schema document.
/// </summary>
public sealed class SchemaDocumentBuilder
{
    private readonly string _name;
    private readonly GroupBuilder _root = new();
    private readonly List<SchemaNode> _nodeDefs = [];
    private readonly List<SchemaDataTypeDef> _dataTypeDefs = [];
    private readonly List<SchemaProcessorDef> _processorDefs = [];
    private readonly List<SchemaDocumentRef> _includes = [];
    private readonly SchemaName? _nodeDef;
    private readonly SchemaValueOrRef<int> _byteSize;
    private readonly List<SchemaProperty> _properties;
    private readonly List<SchemaProcessorRef> _processors;

    public SchemaDocumentBuilder(string name, string? nodeDef = null, SchemaValueOrRef<int> byteSize = default,
        IEnumerable<SchemaProperty>? properties = null, Action<ProcessorBuilder>? processors = null)
    {
        _name = name;
        _nodeDef = SchemaName(nodeDef);
        _byteSize = byteSize;
        _properties = properties?.ToList() ?? [];
        _processors = CreateProcessorRefs(processors);
    }

    /// <summary>
    /// Configures document members using the same typed operations as nested groups.
    /// </summary>
    public SchemaDocumentBuilder Members(Action<GroupBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_root);
        return this;
    }

    public SchemaDocumentBuilder VirtualField(string name, string dataType, int byteOffset = 0, Action<ProcessorBuilder>? processors = null)
    {
        _root.VirtualField(name, dataType, byteOffset, processors);
        return this;
    }

    public SchemaDocumentBuilder OpenChoice(string name, string memberNodeDef, SchemaPipelineMode pipeline = SchemaPipelineMode.Integrate)
    {
        _root.OpenChoice(name, memberNodeDef, pipeline);
        return this;
    }

    public SchemaDocumentBuilder SizedGroup(string name, SchemaValueOrRef<int> byteSize, Action<GroupBuilder>? members = null, Action<ProcessorBuilder>? processors = null)
    {
        _root.SizedGroup(name, byteSize, members, processors);
        return this;
    }

    public SchemaDocumentBuilder RepeatToEnd(string name, Action<GroupBuilder>? members = null, SchemaValueOrRef<int> byteSize = default, Action<ProcessorBuilder>? processors = null)
    {
        _root.RepeatToEnd(name, members, byteSize, processors);
        return this;
    }

    public SchemaDocumentBuilder Field(string name, string? dataType = "sys.uint8", string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
        SchemaValueOrRef<string> value = default, SchemaValueOrRef<int> byteLength = default, int? byteOffset = null)
    {
        _root.Field(name, dataType, nodeDef, processors, value, byteLength, byteOffset);
        return this;
    }
    public SchemaDocumentBuilder BytesField(string name, int byteLength, Action<ProcessorBuilder>? processors = null)
    {
        _root.BytesField(name, byteLength, processors);
        return this;
    }

    public SchemaDocumentBuilder Group(string name, Action<GroupBuilder>? members = null, string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
        SchemaValueOrRef<int> byteSize = default)
    {
        _root.Group(name, members, nodeDef, processors, byteSize);
        return this;
    }

    public SchemaDocumentBuilder Repeat(string name, SchemaValueOrRef<int> count, Action<GroupBuilder>? configure = null, string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
        SchemaValueOrRef<int> byteSize = default, Action<ProcessorBuilder>? valueProcessors = null)
    {
        _root.Repeat(name, count, configure, nodeDef, processors, byteSize, valueProcessors);
        return this;
    }

    public SchemaDocumentBuilder Choice(string name, SchemaValueOrRef<int> selectedIndex, Action<GroupBuilder>? configure = null, string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
        SchemaValueOrRef<int> byteSize = default, Action<ProcessorBuilder>? valueProcessors = null,
        SchemaPipelineMode? pipeline = null, string? memberNodeDef = null)
    {
        _root.Choice(name, selectedIndex, configure, nodeDef, processors, byteSize, valueProcessors, pipeline, memberNodeDef);
        return this;
    }

    /// <summary>
    /// Builds reusable definitions with the same typed operations used for ordinary members.
    /// </summary>
    public SchemaDocumentBuilder NodeDefs(Action<GroupBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new GroupBuilder();
        configure(builder);
        _nodeDefs.AddRange(builder.Members);
        return this;
    }

    public SchemaDocumentBuilder DataTypeDef(string name, string basedOn,
        string? min = null, string? max = null, decimal? scale = null, decimal? shift = null,
        IReadOnlyDictionary<string, string>? options = null, Action<ProcessorBuilder>? processors = null,
        IEnumerable<SchemaProperty>? properties = null, string? valueType = null, string? optionsDefault = null)
    {
        _dataTypeDefs.Add(new SchemaDataTypeDef
        {
            Name = name,
            BasedOn = new SchemaName(basedOn),
            Min = min,
            Max = max,
            Scale = scale,
            Shift = shift,
            Options = options,
            ValueType = valueType is null ? (SchemaName?)null : new SchemaName(valueType),
            OptionsDefault = optionsDefault,
            Processors = CreateProcessorRefs(processors),
            PropertyList = properties?.ToList() ?? [],
        });
        return this;
    }

    public SchemaDocumentBuilder ProcessorDef(string name, string processorKey, params (string Name, string? Value)[] properties)
    {
        var def = new SchemaProcessorDef
        {
            Name = name,
            Processor = new ProcessorKey(processorKey),
        };
        def.PropertyList.AddRange(properties.Select(p => new SchemaProperty { Name = p.Name, Value = p.Value }));
        _processorDefs.Add(def);
        return this;
    }

    public SchemaDocumentBuilder Include(string schema, string? path = null)
    {
        _includes.Add(new SchemaDocumentRef { Schema = schema, Path = path });
        return this;
    }

    public SchemaDocument Build()
    {
        var groups = new List<SchemaGroup>();
        var fields = new List<SchemaField>();
        foreach (var member in _root.Members)
        {
            Collect(member, groups, fields);
        }

        var document = new SchemaDocument
        {
            Name = _name,
            NodeDef = _nodeDef,
            ByteSize = _byteSize,
            PropertyList = _properties.ToList(),
            ProcessorsList = _processors.ToList(),
            NodeDefs = _nodeDefs,
            DataTypeDefs = _dataTypeDefs,
            ProcessorDefs = _processorDefs,
            Includes = _includes,
            Roots = _root.Members.OfType<SchemaGroup>().ToList(),
            Groups = groups,
            Fields = fields,
        };
        document.MemberList.AddRange(_root.Members);
        return document;
    }

    private static void Collect(SchemaNode node, List<SchemaGroup> groups, List<SchemaField> fields)
    {
        switch (node)
        {
            case SchemaField field:
                fields.Add(field);
                break;
            case SchemaGroup group:
                groups.Add(group);
                foreach (var child in group.Members)
                {
                    Collect(child, groups, fields);
                }
                break;
        }
    }

    private static List<SchemaProcessorRef> CreateProcessorRefs(Action<ProcessorBuilder>? processors = null)
    {
        var builder = new ProcessorBuilder();
        processors?.Invoke(builder);
        return builder.Processors;
    }

    public sealed class ProcessorBuilder
    {
        internal List<SchemaProcessorRef> Processors { get; } = [];
        public ProcessorBuilder Add(string processor, params KeyValuePair<string, string?>[] properties)
            => Add(processor, properties, null);

        public ProcessorBuilder Add(string processor, IEnumerable<KeyValuePair<string, string?>> properties, string? publishNamespace)
        {
            var schemaProps = properties.Select(p => new SchemaProperty { Name = p.Key, Value = p.Value }).ToList();
            Processors.Add(new SchemaProcessorRef
            {
                Processor = new SchemaName(processor), PropertyList = schemaProps, PublishNamespace = publishNamespace
            });
            return this;
        }
    }

    public sealed class GroupBuilder
    {
        internal List<SchemaNode> Members { get; } = [];

        public GroupBuilder VirtualField(string name, string dataType, int byteOffset = 0, Action<ProcessorBuilder>? processors = null)
        {
            var field = new SchemaField { Name = name, DataType = new SchemaName(dataType), ByteOffset = byteOffset };
            field.ProcessorsList.AddRange(CreateProcessorRefs(processors));
            Members.Add(field);
            return this;
        }

        public GroupBuilder OpenChoice(string name, string memberNodeDef, SchemaPipelineMode pipeline = SchemaPipelineMode.Integrate)
        {
            Members.Add(new SchemaChoice { Name = name, MemberNodeDef = new SchemaName(memberNodeDef), Pipeline = pipeline });
            return this;
        }

        public GroupBuilder SizedGroup(string name, SchemaValueOrRef<int> byteSize, Action<GroupBuilder>? members = null, Action<ProcessorBuilder>? processors = null)
            => Add(new SchemaGroup { Name = name, ByteSize = byteSize }, members, processors);

        public GroupBuilder RepeatToEnd(string name, Action<GroupBuilder>? members = null, SchemaValueOrRef<int> byteSize = default, Action<ProcessorBuilder>? processors = null)
            => Add(new SchemaRepeat { Name = name, ByteSize = byteSize }, members, processors);

        public GroupBuilder Field(string name, string? dataType = "sys.uint8", string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
            SchemaValueOrRef<string> value = default, SchemaValueOrRef<int> byteLength = default, int? byteOffset = null,
            IEnumerable<SchemaProperty>? properties = null)
        {
            var field = new SchemaField
            {
                Name = name,
                DataType = SchemaName(dataType),
                NodeDef = SchemaName(nodeDef),
                Value = value,
                ByteLength = byteLength,
                ByteOffset = byteOffset,
                PropertyList = properties?.ToList() ?? [],
            };
            field.ProcessorsList.AddRange(CreateProcessorRefs(processors));
            Members.Add(field);
            return this;
        }

        public GroupBuilder BytesField(string name, int byteLength, Action<ProcessorBuilder>? processors = null)
        {
            var field = new SchemaField
            {
                Name = name,
                DataType = SchemaName("sys.bytes"),
                ByteLength = byteLength,
            };
            field.ProcessorsList.AddRange(CreateProcessorRefs(processors));
            Members.Add(field);
            return this;
        }

        public GroupBuilder Group(string name, Action<GroupBuilder>? members = null, string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
            SchemaValueOrRef<int> byteSize = default, IEnumerable<SchemaProperty>? properties = null)
            => Add(new SchemaGroup
            {
                Name = name, NodeDef = SchemaName(nodeDef), ByteSize = byteSize, PropertyList = properties?.ToList() ?? []
            }, members, processors);

        public GroupBuilder Repeat(string name, SchemaValueOrRef<int> count, Action<GroupBuilder>? members = null, string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
            SchemaValueOrRef<int> byteSize = default, Action<ProcessorBuilder>? valueProcessors = null,
            IEnumerable<SchemaProperty>? properties = null)
            => Add(new SchemaRepeat
            {
                Name = name, Count = count, NodeDef = SchemaName(nodeDef), ByteSize = byteSize,
                PropertyList = properties?.ToList() ?? [],
                ValueProcessorsList = CreateProcessorRefs(valueProcessors)
            }, members, processors);

        /// <summary>
        /// The members of a choice are its alternatives (typically groups).
        /// </summary>
        public GroupBuilder Choice(string name, SchemaValueOrRef<int> selectedIndex, Action<GroupBuilder>? members = null, string? nodeDef = null, Action<ProcessorBuilder>? processors = null,
            SchemaValueOrRef<int> byteSize = default, Action<ProcessorBuilder>? valueProcessors = null,
            SchemaPipelineMode? pipeline = null, string? memberNodeDef = null, IEnumerable<SchemaProperty>? properties = null)
            => Add(new SchemaChoice
            {
                Name = name, SelectedIndex = selectedIndex, NodeDef = SchemaName(nodeDef), ByteSize = byteSize,
                PropertyList = properties?.ToList() ?? [],
                PipelineOverride = pipeline, MemberNodeDef = SchemaName(memberNodeDef),
                ValueProcessorsList = CreateProcessorRefs(valueProcessors)
            }, members, processors);

        private GroupBuilder Add(SchemaGroup group, Action<GroupBuilder>? members, Action<ProcessorBuilder>? processors = null)
        {
            group.ProcessorsList.AddRange(CreateProcessorRefs(processors));
            if (members is not null)
            {
                var builder = new GroupBuilder();
                members(builder);
                group.MemberList.AddRange(builder.Members);
            }
            Members.Add(group);
            return this;
        }
    }

    private static SchemaName? SchemaName(string? name)
    {
        if (name is null) return null;
        return new SchemaName(name);
    }
}
