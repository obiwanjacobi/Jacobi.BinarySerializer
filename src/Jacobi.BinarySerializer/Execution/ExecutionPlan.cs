using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Microsoft.Extensions.Logging;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// The Schema is compiled into a ExecutionPlan.
/// A set of instructions for the Serializer to follow when serializing and deserializing data.
/// </summary>
public sealed class ExecutionPlan
{
    // schema hierarchy with immutable GroupInfo and FieldInfo objects
    // resolved Processor pipelines for each field and group
    public required GroupInfo Root { get; init; }

    /// <summary>
    /// The name of the schema root the plan was created for (e.g. 'Png.File'); null for a plan created from a group.
    /// </summary>
    public SchemaName? Name { get; internal set; }

    /// <summary>
    /// The data types the plan was built with.
    /// </summary>
    public DataTypeRegistry DataTypes { get; init; } = DataTypeRegistry.CreateDefault();

    private IReadOnlyDictionary<ChoiceInfo, IReadOnlyDictionary<SchemaName, GroupInfo>> _openChoiceBodies
        = new Dictionary<ChoiceInfo, IReadOnlyDictionary<SchemaName, GroupInfo>>();

    internal void SetOpenChoiceBodies(IReadOnlyDictionary<ChoiceInfo, IReadOnlyDictionary<SchemaName, GroupInfo>> bodies)
        => _openChoiceBodies = bodies;

    /// <summary>
    /// Finds the plan of the group a value model selected for an open choice.
    /// The group must be a schema root that conforms to the nodeDef of the open choice.
    /// </summary>
    internal GroupInfo ResolveOpenChoiceBody(ChoiceInfo choice, SchemaName selected, SchemaPath target = default)
    {
        if (_openChoiceBodies.TryGetValue(choice, out var bodies))
        {
            if (String.IsNullOrEmpty(selected.Name) && !target.IsEmpty)
            {
                selected = new SchemaName(target.Segments.First());
            }
            if (bodies.TryGetValue(selected, out var exact))
            {
                return exact;
            }
            if (String.IsNullOrEmpty(selected.Namespace))
            {
                var matches = bodies.Where(b => String.Equals(b.Key.Name, selected.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    return matches[0].Value;
                }
            }
        }

        var contract = ((SchemaChoice)choice.Group).MemberNodeDef;
        throw new InvalidOperationException(
            $"'{choice.Path}': the group '{selected}' provided by the client-model, is not a known group that conforms to the nodeDef '{contract}' of the open choice.");
    }

    /// <summary>
    /// Creates the range of the target nodes inside a body of an open choice (null for an empty from-target: the whole body):
    /// from the first field of <paramref name="from"/> up to the last field of <paramref name="to"/>
    /// (an empty to-target: the end of <paramref name="from"/>, at the items of <paramref name="fromInstance"/>).
    /// The instances select the items of the repeats around the targets inside the body (empty: from the first, up to the last item).
    /// </summary>
    internal PlanRange? CreateOpenChoiceRange(ChoiceInfo choice, GroupInfo body,
        SchemaPath from, InstancePath fromInstance = default, SchemaPath to = default, InstancePath toInstance = default)
    {
        if (from.IsEmpty)
        {
            if (!to.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"'{choice.Path}': the target end '{to}' is given without a target start.");
            }
            return null;
        }

        if (to.IsEmpty)
        {
            to = from;
            toInstance = fromInstance;
        }

        var first = FindTarget(choice, body, from);
        var last = FindTarget(choice, body, to);
        try
        {
            return new PlanRange(this, first, fromInstance, last, toInstance);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"'{choice.Path}': the target '{from}{fromInstance}' to '{to}{toInstance}' is not a range inside the selected group '{body.Path}': {exception.Message}", exception);
        }
    }

    private static NodeInfo FindTarget(ChoiceInfo choice, GroupInfo body, SchemaPath target)
        => Find(body, target) is { } node
            ? node
            : throw new InvalidOperationException(
                $"'{choice.Path}': the target '{target}' is not a node inside the selected group '{body.Path}'.");

    /// <summary>
    /// Checks the open choices a read or write visits: an open choice outside the range is skipped,
    /// an open choice inside the range must have at least one group in the plan that conforms to its memberNodeDef.
    /// </summary>
    internal void ValidateOpenChoices(PlanRange? range, ILogger logger)
        => ValidateOpenChoices(Root, range, logger);

    private void ValidateOpenChoices(GroupInfo group, PlanRange? range, ILogger logger)
    {
        foreach (var member in group.Members)
        {
            if (member is ChoiceInfo { IsOpen: true } open)
            {
                var contract = ((SchemaChoice)open.Group).MemberNodeDef!.Value;
                if (range is not null && !range.IncludesAny(open))
                {
                    continue;
                }

                if (!_openChoiceBodies.TryGetValue(open, out var bodies) || bodies.Count == 0)
                {
                    throw logger.Fail(
                        $"'{open.Path}': no group in the plan conforms to the nodeDef '{contract}' of the open choice.");
                }
            }
            else if (member is GroupInfo child)
            {
                ValidateOpenChoices(child, range, logger);
            }
        }
    }

    /// <summary>Finds a node by its path (e.g. 'Root.Header.Length'); null when there is none.</summary>
    public NodeInfo? Find(SchemaPath path)
        => Find(Root, path);

    private static NodeInfo? Find(NodeInfo node, SchemaPath path)
    {
        if (node.Path == path)
        {
            return node;
        }

        if (node is GroupInfo group)
        {
            foreach (var child in group.Members)
            {
                if (Find(child, path) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Creates a range of fields to write or read: from the first field of <paramref name="fromPath"/> to the last field of <paramref name="toPath"/>.
    /// Each path is a field or a group (a group stands for its first or last field).
    /// </summary>
    public PlanRange CreateRange(SchemaPath fromPath, SchemaPath toPath)
    {
        var from = Find(fromPath) ?? throw new ArgumentException($"There is no node '{fromPath}' in the plan.", nameof(fromPath));
        var to = Find(toPath) ?? throw new ArgumentException($"There is no node '{toPath}' in the plan.", nameof(toPath));
        return new PlanRange(this, from, to);
    }

    /// <summary>
    /// Creates a range that covers all fields of the group at <paramref name="groupPath"/>.
    /// </summary>
    public PlanRange CreateRange(SchemaPath groupPath)
    {
        var node = Find(groupPath) ?? throw new ArgumentException($"There is no node '{groupPath}' in the plan.", nameof(groupPath));
        if (node is not GroupInfo)
        {
            throw new ArgumentException($"Node '{groupPath}' is not a group.", nameof(groupPath));
        }
        return new PlanRange(this, node, node);
    }

    /// <summary>
    /// Creates a range whose bounds are inside repeats: 
    /// of each repeat on the way (outermost first). Missing indices mean the first (from) or last (to) item.
    /// </summary>
    public PlanRange CreateRange(SchemaPath fromPath, InstancePath fromInstance, SchemaPath toPath, InstancePath toInstance)
    {
        var from = Find(fromPath) ?? throw new ArgumentException($"There is no node '{fromPath}' in the plan.", nameof(fromPath));
        var to = Find(toPath) ?? throw new ArgumentException($"There is no node '{toPath}' in the plan.", nameof(toPath));
        return new PlanRange(this, from, fromInstance, to, toInstance);
    }

    public static ExecutionPlan Create(SchemaSet schemas, SchemaName schemaName, IProcessorProvider processorProvider, DataTypeRegistry? dataTypes = null)
        => Create(schemas, schemas.FindRoot(schemaName), processorProvider, dataTypes);

    /// <summary>
    /// Creates the plan of any group of <paramref name="schemas"/>: a root, a node def or a group nested in one of them.
    /// The plan is named after the group in its document; the open choices in it resolve their bodies in all <paramref name="schemas"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The group is not part of <paramref name="schemas"/>.</exception>
    public static ExecutionPlan Create(SchemaSet schemas, SchemaGroup group, IProcessorProvider processorProvider, DataTypeRegistry? dataTypes = null)
    {
        var document = schemas.FindDocument(group)
            ?? throw new ArgumentException($"The group '{group.Name}' is not part of the schemas.", nameof(group));
        dataTypes ??= DataTypeRegistry.CreateDefault();
        dataTypes.RegisterDataTypeDefs(schemas.Documents);
        var builder = new ExecutionPlanBuilder(processorProvider, dataTypes);
        var plan = builder.Build(group);
        plan.Name = new SchemaName($"{document.Name}{SchemaName.Separator}{group.Name}");
        plan.SetOpenChoiceBodies(builder.BuildOpenChoiceBodies(plan.Root, schemas, document.Name));
        return plan;
    }

    public static ExecutionPlan Create(SchemaGroup root, IProcessorProvider processorProvider, DataTypeRegistry? dataTypes = null)
    {
        var builder = new ExecutionPlanBuilder(processorProvider, dataTypes);
        return builder.Build(root);
    }

    public static ExecutionPlan Create(SchemaDocument document, SchemaName schemaName, IProcessorProvider processorProvider, DataTypeRegistry? dataTypes = null)
    {
        var builder = new ExecutionPlanBuilder(processorProvider, dataTypes);
        var root = ExecutionPlanBuilder.FindRoot(document, schemaName);
        var plan = builder.Build(root);
        plan.Name = schemaName;
        return plan;
    }
}

//-----------------------------------------------------------------------------

internal sealed class ExecutionPlanBuilder(IProcessorProvider processorProvider, DataTypeRegistry? dataTypes = null)
{
    private readonly DataTypeRegistry _dataTypes = dataTypes ?? DataTypeRegistry.CreateDefault();

    public ExecutionPlan Build(SchemaGroup root, ProcessorPipeline? parentPipeline = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var state = new BuildState { Root = root };
        var rootInfo = BuildGroup(root, root.Name, parentPipeline, state);
        ResolvePathReferences(rootInfo, state);
        ValidateBytesWindow(rootInfo, false, state);

        if (state.Errors.Count > 0)
        {
            throw new ExecutionPlanException(root.Name, state.Errors);
        }

        return new ExecutionPlan { Root = rootInfo, DataTypes = _dataTypes };
    }

    /// <summary>
    /// Builds the plans of all schema roots that conform (declare the nodeDef) to each open choice, including nested open choices.
    /// </summary>
    internal IReadOnlyDictionary<ChoiceInfo, IReadOnlyDictionary<SchemaName, GroupInfo>> BuildOpenChoiceBodies(
        GroupInfo root, SchemaSet schemas, string? rootNamespace)
    {
        var result = new Dictionary<ChoiceInfo, IReadOnlyDictionary<SchemaName, GroupInfo>>();
        var built = new Dictionary<(SchemaName, IReadOnlyList<ProcessorBinding>?, IReadOnlyList<ProcessorBinding>?, IReadOnlyList<ProcessorBinding>?, IReadOnlyList<ProcessorBinding>?), GroupInfo>();
        var work = new Queue<(GroupInfo Group, string? Namespace)>();
        work.Enqueue((root, rootNamespace));

        while (work.Count > 0)
        {
            var (group, ns) = work.Dequeue();
            foreach (var choice in FindOpenChoices(group))
            {
                var contract = ((SchemaChoice)choice.Group).MemberNodeDef!.Value.Qualify(ns);
                var bodies = new Dictionary<SchemaName, GroupInfo>();
                foreach (var document in schemas.Documents)
                {
                    foreach (var candidate in document.Roots)
                    {
                        if (candidate.NodeDef is not { } nodeDef || nodeDef.Qualify(document.Name) != contract)
                        {
                            continue;
                        }

                        var name = new SchemaName($"{document.Name}{SchemaName.Separator}{candidate.Name}");
                        var pipeline = ((SchemaChoice)choice.Group).Pipeline == SchemaPipelineMode.Integrate ? choice.Pipeline : null;
                        var key = (name, pipeline?.ValueProcessors, pipeline?.FieldProcessors, pipeline?.LayoutProcessors, pipeline?.StreamProcessors);
                        if (!built.TryGetValue(key, out var body))
                        {
                            body = Build(candidate, pipeline is null ? null : new ProcessorPipeline(pipeline, [])).Root;
                            if (body is RepeatInfo or ChoiceInfo)
                            {
                                throw new InvalidOperationException(
                                    $"'{choice.Path}': the group '{name}' must be a plain group to be used in an open choice.");
                            }
                            built[key] = body;
                            work.Enqueue((body, document.Name));
                        }
                        bodies[name] = body;
                    }
                }
                result[choice] = bodies;
            }
        }
        return result;
    }

    private static IEnumerable<ChoiceInfo> FindOpenChoices(GroupInfo group)
    {
        foreach (var member in group.Members)
        {
            if (member is ChoiceInfo { IsOpen: true } open)
            {
                yield return open;
            }
            else if (member is GroupInfo child)
            {
                foreach (var nested in FindOpenChoices(child))
                {
                    yield return nested;
                }
            }
        }
    }

    internal static SchemaGroup FindRoot(SchemaDocument document, SchemaName root)
    {
        var matches = new[] { document }
            .SelectMany(d => d.Roots.Select(r => (Document: d, Root: r)))
            .Where(m => String.Equals(m.Root.Name, root.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException($"Schema root '{root}' not found.");
        if (matches.Count > 1)
            throw new InvalidOperationException($"Schema root '{root}' is ambiguous; qualify it with a namespace.");
        if (!matches[0].Document.IsCompiled)
            throw new InvalidOperationException($"Schema document '{matches[0].Document.Name}' is not compiled.");

        return matches[0].Root;
    }

    private NodeInfo? BuildNode(SchemaNode node, SchemaPath path, ProcessorPipeline parentPipeline, BuildState state)
        => node switch
        {
            SchemaField field => BuildField(field, path, parentPipeline, state),
            SchemaGroup group => BuildGroup(group, path, parentPipeline, state),
            _ => state.Error<NodeInfo>(path, $"Unsupported schema node kind '{node.Kind}'.")
        };

    private FieldInfo? BuildField(SchemaField field, SchemaPath path, ProcessorPipeline parentPipeline, BuildState state)
    {
        var processors = BindProcessors(field.Processors, path, state);
        var fieldPipeline = CreatePipeline(parentPipeline, processors);
        var declaresSemantics = fieldPipeline.ValueProcessors.Count > 0;
        if (!declaresSemantics && BindDataTypeProcessor(field, path, state) is { } dataTypeProcessor)
        {
            fieldPipeline = CreatePipeline(parentPipeline, [.. processors, dataTypeProcessor]);
        }
        if (!ReferenceEquals(fieldPipeline.FieldProcessors, parentPipeline.FieldProcessors))
        {
            ValidateFieldChain(fieldPipeline.FieldProcessors, path, state);
        }
        if (!ReferenceEquals(fieldPipeline.LayoutProcessors, parentPipeline.LayoutProcessors))
        {
            ValidateLayoutChain(fieldPipeline.LayoutProcessors, path, state);
        }
        return BuildFieldInfo(field, path, fieldPipeline, declaresSemantics, state);
    }

    /// <summary>
    /// The default semantic processor ('sys.datatype') of a field whose data type has facets (e.g. a dataTypeDef with a scale),
    /// so the facets apply without declaring a processor. Not used when the field (or its group) has a semantic processor.
    /// </summary>
    private ProcessorBinding? BindDataTypeProcessor(SchemaField field, SchemaPath path, BuildState state)
    {
        if (field.DataType is not { } fieldDataType || !_dataTypes.TryGet(fieldDataType, out var dataType) || !dataType.HasFacets)
        {
            return null;
        }
        if (!processorProvider.TryCreateProcessor(DataTypeProcessorKey, out var processor) || processor is not IValueProcessor)
        {
            return state.Error<ProcessorBinding>(path,
                $"The data type '{dataType}' has facets that need the '{DataTypeProcessorKey}' processor (register the 'sys' processors) or a semantic processor on the field.");
        }
        return new(processor, [], null);
    }

    private static readonly ProcessorKey DataTypeProcessorKey = new("sys", "datatype");

    private FieldInfo? BuildFieldInfo(SchemaField field, SchemaPath path, ProcessorPipeline fieldPipeline, bool declaresSemantics, BuildState state)
    {
        var length = BindValueSource(field.ByteLength, path, state);
        if (field.DataType is not { } fieldDataType)
        {
            state.Error(path, "The field has no data type (and no nodeDef that defines one).");
            return null;
        }
        if (!_dataTypes.TryGet(fieldDataType, out var dataType))
        {
            state.Error(path, $"The data type '{fieldDataType}' is not registered.");
            return null;
        }
        if (!dataType.SupportsLength && length is int or PublishedValueKey)
        {
            state.Error(path, $"A length is not supported on a {dataType} field.");
        }
        if (length is int constantLength && dataType.FixedSize is { } fixedSize && fixedSize != constantLength && fieldPipeline.FieldProcessors.Count == 0)
        {
            state.Error(path, $"The length {constantLength} does not match the {fixedSize} bytes of a {dataType} field; a field processor is needed to represent it.");
        }
        var valueType = declaresSemantics ? null : dataType.ValueType;
        return new FieldInfo
        {
            Name = field.Name,
            Path = path,
            Field = field,
            DataType = dataType,
            ValueType = valueType,
            Pipeline = fieldPipeline,
            ByteLength = length,
            ByteOffset = field.ByteOffset,
            // the constant is a logical value
            ConstantValue = BindConstant(field, valueType ?? dataType, path, state),
            ValueReference = BindValueReference(field, path, state),
        };
    }

    private static object? BindConstant(SchemaField field, DataTypeDescriptor dataType, SchemaPath path, BuildState state)
    {
        if (field.Value is not string text)
        {
            return null;
        }

        if (!dataType.Parse(text, out var constant))
        {
            state.Error(path, $"The constant '{text}' is not a valid {dataType} value.");
        }
        return constant;
    }

    private static PublishedValueKey? BindValueReference(SchemaField field, SchemaPath path, BuildState state)
        => field.Value switch
        {
            SchemaNodeRef nodeRef => BindKey(nodeRef, path, state),
            SchemaPubRef pubRef => new PublishedValueKey(pubRef.Namespace, pubRef.Name),
            _ => null,
        };

    private GroupInfo BuildGroup(SchemaGroup group, SchemaPath path, ProcessorPipeline? parentPipeline, BuildState state)
    {
        var processors = BindProcessors(group.Processors, path, state);
        var pipeline = CreatePipeline(parentPipeline, processors);
        if (parentPipeline is null || !ReferenceEquals(pipeline.FieldProcessors, parentPipeline.FieldProcessors))
        {
            ValidateFieldChain(pipeline.FieldProcessors, path, state);
        }
        if (parentPipeline is null || !ReferenceEquals(pipeline.LayoutProcessors, parentPipeline.LayoutProcessors))
        {
            ValidateLayoutChain(pipeline.LayoutProcessors, path, state);
        }
        var members = new List<NodeInfo>(group.Members.Count);
        foreach (var child in group.Members)
        {
            if (BuildNode(child, path.Append(child.Name), pipeline, state) is { } childInfo)
                members.Add(childInfo);
        }

        GroupInfo info = group switch
        {
            SchemaRepeat repeat => new RepeatInfo
            {
                Name = group.Name,
                Path = path,
                Group = group,
                Members = members,
                Pipeline = pipeline,
                Count = BindValueSource(repeat.Count, path, state),
                ByteSize = BindValueSource(repeat.ByteSize, path, state),
                ValueProcessors = BindValueProcessors(repeat.ValueProcessors, path, state),
            },
            SchemaChoice choice => new ChoiceInfo
            {
                Name = group.Name,
                Path = path,
                Group = group,
                Members = members,
                Pipeline = pipeline,
                SelectedIndex = BindValueSource(choice.SelectedIndex, path, state),
                ByteSize = BindValueSource(choice.ByteSize, path, state),
                ValueProcessors = BindValueProcessors(choice.ValueProcessors, path, state),
            },
            _ => new GroupInfo
            {
                Name = group.Name,
                Path = path,
                Group = group,
                Members = members,
                Pipeline = pipeline,
                ByteSize = BindValueSource(group.ByteSize, path, state),
            },
        };

        if (info is ChoiceInfo { SelectedIndex: int index } && (index < 0 || index >= members.Count))
        {
            state.Error(path, $"Choice index {index} is out of range (0..{members.Count - 1}).");
        }

        for (var i = 0; i < members.Count; i++)
        {
            if (info is not ChoiceInfo && i < members.Count - 1 && TakesRestOfWindow(members[i]))
            {
                state.Error(members[i].Path, "A node that takes the rest of the size window (a bytes field without a length, or a group or choice that ends in one) must be the last node of its group.");
            }
            members[i].Parent = info;
            members[i].Index = i;
        }

        return info;
    }

    /// <summary>
    /// True when the count or selected index of the group (the value its value processors transform) refers to <paramref name="target"/>.
    /// </summary>
    private static bool IsValueKey(GroupInfo group, SchemaPath target)
    {
        var source = group switch
        {
            RepeatInfo repeat => repeat.Count,
            ChoiceInfo choice => choice.SelectedIndex,
            _ => default,
        };
        return source is PublishedValueKey key && key.Namespace.Length == 0 && key.Name == target.Value;
    }

    private static void ValidateBytesWindow(NodeInfo node, bool inWindow, BuildState state)
    {
        switch (node)
        {
            case FieldInfo { DataType.TakesRestOfWindow: true, HasByteLength: false } when !inWindow:
                state.Error(node.Path, "A bytes field without a length takes the rest of the enclosing size window, but no enclosing group has a size.");
                break;
            case GroupInfo group:
                foreach (var child in group.Members)
                {
                    ValidateBytesWindow(child, inWindow || group.HasByteSize, state);
                }
                break;
        }
    }

    // a repeat without a count may be followed by other nodes: the value model ends it (checked when reading).
    private static bool TakesRestOfWindow(NodeInfo node)
        => node switch
        {
            GroupInfo { HasByteSize: true } => false,
            FieldInfo { DataType.TakesRestOfWindow: true, HasByteLength: false } => true,
            RepeatInfo => false,
            ChoiceInfo choice => choice.Members.Any(TakesRestOfWindow),
            GroupInfo group => group.Members.Count > 0 && TakesRestOfWindow(group.Members[^1]),
            _ => false,
        };

    private static ProcessorPipeline CreatePipeline(ProcessorPipeline? parent, IReadOnlyList<ProcessorBinding> processors)
    {
        if (processors.Count == 0)
        {
            return parent ?? new ProcessorPipeline([]);
        }
        return new ProcessorPipeline(parent, processors);
    }

    private List<ProcessorBinding> BindProcessors(IReadOnlyList<SchemaProcessorRef> refs, string path, BuildState state)
    {
        var bindings = new List<ProcessorBinding>(refs.Count);
        foreach (var processorRef in refs)
        {
            if (Bind(processorRef, path, state) is { } binding)
            {
                bindings.Add(binding);
            }
        }
        return bindings;
    }

    private ProcessorBinding? Bind(SchemaProcessorRef processorRef, string path, BuildState state)
    {
        if (processorRef.Key is not { } key)
        {
            return state.Error<ProcessorBinding>(path,
                $"Processor '{processorRef.Processor}' does not designate a processor (unresolved reference or missing namespace).");
        }

        if (!processorProvider.TryCreateProcessor(key, out var processor))
        {
            return state.Error<ProcessorBinding>(path, $"Unknown processor '{key}'.");
        }

        var implementsStage = processor.Stage switch
        {
            PipelineStage.Semantic => processor is IValueProcessor,
            PipelineStage.Representation => processor is IFieldProcessor,
            PipelineStage.Layout => processor is ILayoutProcessor,
            PipelineStage.Stream => processor is IStreamProcessor,
            _ => false
        };

        if (!implementsStage)
        {
            return state.Error<ProcessorBinding>(path,
                $"Processor '{processor.Key}' declares stage {processor.Stage} but does not implement its stage interface.");
        }

        ValidateProperties(processor, processorRef.EffectiveProperties, path, state);
        return new(processor, processorRef.EffectiveProperties, processorRef.PublishNamespace);
    }

    private void ValidateProperties(IProcessor processor, IReadOnlyList<SchemaProperty> properties, string path, BuildState state)
    {
        var lookup = new ProcessorProperties(properties, processor.Key, _dataTypes);
        foreach (var descriptor in processor.Properties)
        {
            if (!_dataTypes.TryGet(descriptor.DataType, out var dataType))
            {
                state.Error(path, $"The data type '{descriptor.DataType}' of the '{lookup.FullName(descriptor.Name)}' property is not registered.");
                continue;
            }

            var property = lookup.Find(descriptor.Name);
            if (property is null)
            {
                if (descriptor.IsRequired)
                {
                    state.Error(path, $"The '{lookup.FullName(descriptor.Name)}' property is required by the '{processor.Key}' processor.");
                }
            }
            else if (!dataType.Parse(property.Value, out _))
            {
                state.Error(path, $"The '{lookup.FullName(descriptor.Name)}' value '{property.Value}' is not a valid {dataType} value.");
            }
        }
    }

    /// <summary>Every layout processor outside the innermost one wraps bytes and must implement the chained byte interfaces.</summary>
    private static void ValidateLayoutChain(IReadOnlyList<ProcessorBinding> chain, string path, BuildState state)
    {
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var processor = chain[i].Processor;
            if (processor is not ILayoutWriter<ReadOnlySpan<byte>> || processor is not ILayoutReader<ReadOnlyMemory<byte>>)
            {
                state.Error(path,
                    $"Processor '{processor.Key}' cannot wrap '{chain[i + 1].Processor.Key}' in a layout chain: it does not implement the chained layout interfaces.");
            }
        }
    }

    /// <summary>A field chain is the head (first) followed by processors that implement the chained (EncodedField to EncodedField) interfaces.</summary>
    private static void ValidateFieldChain(IReadOnlyList<ProcessorBinding> chain, string path, BuildState state)
    {
        for (var i = 1; i < chain.Count; i++)
        {
            var processor = chain[i].Processor;
            if (processor is not IFieldWriter<EncodedField, EncodedField> || processor is not IFieldReader<EncodedField, EncodedField>)
            {
                var previous = chain[i - 1].Processor.Key;
                state.Error(path,
                    $"Processor '{processor.Key}' cannot follow '{previous}' in a field chain: it does not implement the chained field interfaces.");
            }
        }
    }

    private List<ProcessorBinding> BindValueProcessors(IReadOnlyList<SchemaProcessorRef> refs, string path, BuildState state)
    {
        var bindings = BindProcessors(refs, path, state);
        foreach (var binding in bindings)
        {
            if (binding.Processor.Stage != PipelineStage.Semantic)
            {
                state.Error(path, $"Processor '{binding.Processor.Key}' is not a value (semantic) processor and cannot process a repeat count or choice index.");
            }
        }
        return bindings;
    }

    private ValueSource<int> BindValueSource(SchemaValueOrRef<int> source, string path, BuildState state)
        => source switch
        {
            int value => value,
            SchemaNodeRef nodeRef => BindNodeRef(nodeRef, path, state),
            SchemaPubRef pubRef => new PublishedValueKey(pubRef.Namespace, pubRef.Name),
            _ => default,   // error already reported (or unresolved)
        };

    private static ValueSource<int> BindNodeRef(SchemaNodeRef nodeRef, SchemaPath path, BuildState state)
        => BindKey(nodeRef, path, state) is { } key ? key : default;

    private static PublishedValueKey? BindKey(SchemaNodeRef nodeRef, SchemaPath path, BuildState state)
    {
        if (string.IsNullOrWhiteSpace(nodeRef.Path))
        {
            state.Error(path, "A value reference cannot be empty.");
            return null;
        }

        if (nodeRef.Resolve(path) is not { } resolved)
        {
            state.Error(path, $"Value reference '{nodeRef}' goes up beyond the root of the schema.");
            return null;
        }

        var target = new SchemaPath(resolved.Path);
        state.PathReferences.Add((path, target));
        var instance = nodeRef.IsRelative
            ? BindRelativeInstance(nodeRef, target, path, state)
            : BindInstance(nodeRef, target, path, state);
        return PublishedValueKey.ForPath(target, instance);
    }

    /// <summary>
    /// Builds the instance template of a relative reference: it keeps the instance of the referrer, so every repeat on the way
    /// to the target is the item of the referrer (<see cref="InstancePath.Current"/>). A repeat that does not hold the referrer
    /// cannot be reached: which of its items is meant would need an index.
    /// </summary>
    private static InstancePath BindRelativeInstance(SchemaNodeRef nodeRef, SchemaPath target, SchemaPath referrer, BuildState state)
    {
        if (state.RepeatsOnPath(target) is not { } repeats)
        {
            return default;   // unknown target: reported when the path references are resolved
        }

        var template = new List<int>(repeats.Count);
        foreach (var repeat in repeats)
        {
            if (!referrer.IsSameOrDescendantOf(repeat) || referrer.Equals(repeat))
            {
                state.Error(referrer, $"Value reference '{nodeRef}' reaches into the repeat '{repeat}' that does not hold the referrer: "
                    + "use an absolute path with an instance index.");
            }
            template.Add(InstancePath.Current);
        }

        return new InstancePath(template);
    }

    /// <summary>
    /// Builds the instance template of an absolute reference: one entry per repeat on the way to the target (outermost first),
    /// the explicit index, <see cref="InstancePath.Current"/> for '[]', or 0 (the first item) when no index is given.
    /// </summary>
    private static InstancePath BindInstance(SchemaNodeRef nodeRef, SchemaPath target, SchemaPath referrer, BuildState state)
    {
        if (state.RepeatsOnPath(target) is not { } repeats)
        {
            return default;   // unknown target: reported when the path references are resolved
        }

        foreach (var index in nodeRef.Indices)
        {
            if (!repeats.Any(r => r.Value == index.Node))
            {
                state.Error(referrer, $"Value reference '{nodeRef}': '{index.Node}' is not a repeat on the path to '{target}'.");
            }
        }

        var template = new List<int>(repeats.Count);
        foreach (var repeat in repeats)
        {
            var found = nodeRef.Indices.Where(i => i.Node == repeat.Value).Select(i => (SchemaInstanceIndex?)i).FirstOrDefault();
            if (found is not { } instance)
            {
                // no index given for this repeat: use the first item (0)
                template.Add(0);
            }
            else if (instance.IsCurrent)
            {
                if (!referrer.IsSameOrDescendantOf(repeat) || referrer.Equals(repeat))
                {
                    state.Error(referrer, $"Value reference '{nodeRef}': '[]' on '{repeat}' is only valid for a node inside that repeat.");
                }
                template.Add(InstancePath.Current);
            }
            else
            {
                template.Add(instance.Index);
            }
        }

        return new InstancePath(template);
    }

    private static void ResolvePathReferences(GroupInfo root, BuildState state)
    {
        foreach (var (from, target) in state.PathReferences)
        {
            switch (Find(root, target))
            {
                case FieldInfo field:
                    field.PublishesValue = true;
                    if (Find(root, from) is GroupInfo { ValueProcessors.Count: > 0 } valueGroup && IsValueKey(valueGroup, target))
                    {
                        valueGroup.FieldRefValueType = field.ValueType;
                    }
                    if (Find(root, from) is RepeatInfo { Count: PublishedValueKey countKey } repeat
                        && countKey.Namespace.Length == 0 && countKey.Name == target.Value
                        && repeat.Parent is not null && ReferenceEquals(repeat.Parent, field.Parent))
                    {
                        field.CountOf = repeat;
                    }
                    if (Find(root, from) is GroupInfo { HasByteSize: true, ByteSize: PublishedValueKey sizeKey } sized
                        && sizeKey.Namespace.Length == 0 && sizeKey.Name == target.Value
                        && sized.Parent is not null && ReferenceEquals(sized.Parent, field.Parent))
                    {
                        field.ByteSizeOf = sized;
                    }
                    if (Find(root, from) is FieldInfo { ByteLength: PublishedValueKey lengthKey } bytesField
                        && lengthKey.Namespace.Length == 0 && lengthKey.Name == target.Value
                        && bytesField.Parent is not null && ReferenceEquals(bytesField.Parent, field.Parent))
                    {
                        field.ByteLengthOf = bytesField;
                    }
                    break;
                case null:
                    state.Error(from, $"Value reference '{target}' does not match any node in the schema.");
                    break;
                default:
                    state.Error(from, $"Value reference '{target}' must refer to a field, not a group.");
                    break;
            }
        }
    }

    private static NodeInfo? Find(NodeInfo node, SchemaPath path)
    {
        if (node.Path == path)
        {
            return node;
        }

        if (node is GroupInfo group)
        {
            foreach (var child in group.Members)
            {
                if (Find(child, path) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private sealed class BuildState
    {
        public SchemaGroup? Root { get; init; }

        /// <summary>The repeats (outermost first) on the way to <paramref name="target"/>; null when the target is not in the schema.</summary>
        public List<SchemaPath>? RepeatsOnPath(SchemaPath target)
        {
            var segments = target.Segments.ToArray();
            if (Root is null || segments.Length == 0 || segments[0] != Root.Name)
            {
                return null;
            }

            var repeats = new List<SchemaPath>();
            SchemaNode node = Root;
            var current = new SchemaPath(Root.Name);
            if (Root is SchemaRepeat)
            {
                repeats.Add(current);
            }
            for (var i = 1; i < segments.Length; i++)
            {
                if (node is not SchemaGroup group)
                {
                    return null;
                }

                var child = group.Members.FirstOrDefault(c => c.Name == segments[i]);
                if (child is null)
                {
                    return null;
                }

                node = child;
                current = current.Append(segments[i]);
                if (node is SchemaRepeat)
                {
                    repeats.Add(current);
                }
            }
            return repeats;
        }
        public List<(SchemaPath From, SchemaPath Target)> PathReferences { get; } = [];

        public List<string> Errors { get; } = [];

        public void Error(string path, string message) => Errors.Add($"{path}: {message}");

        public void Error(string type, string path, string message) => Errors.Add($"{path}: {message} [{type}]");

        public T? Error<T>(string path, string message) where T : class
        {
            Error(typeof(T).Name, path, message);
            return null;
        }
    }
}

public sealed class ExecutionPlanException(string root, IReadOnlyList<string> errors)
    : Exception($"Failed to build execution plan for '{root}':{Environment.NewLine}{String.Join(Environment.NewLine, errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
