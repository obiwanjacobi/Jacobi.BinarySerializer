using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

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
            foreach (var child in group.Children)
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
        return new PlanRange(from, to);
    }
}

//-----------------------------------------------------------------------------

public sealed class ExecutionPlanBuilder(IProcessorProvider processorProvider)
{
    public ExecutionPlan Build(SchemaSet schemaSet, SchemaName root)
    {
        ArgumentNullException.ThrowIfNull(schemaSet);
        return Build(FindRoot(schemaSet, root));
    }

    // TODO: add overload ExecutionPlan Build(SchemaDocument, SchemaName/string root){}

    public ExecutionPlan Build(SchemaGroup root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var state = new BuildState();
        var rootInfo = BuildGroup(root, root.Name, parentPipeline: null, state);
        ResolvePathReferences(rootInfo, state);

        if (state.Errors.Count > 0)
        {
            throw new ExecutionPlanException(root.Name, state.Errors);
        }

        return new ExecutionPlan { Root = rootInfo };
    }

    private static SchemaGroup FindRoot(SchemaSet schemaSet, SchemaName root)
    {
        var documents = String.IsNullOrEmpty(root.Namespace)
            ? schemaSet.Documents
            : schemaSet.Documents.Where(d => String.Equals(d.Name, root.Namespace, StringComparison.OrdinalIgnoreCase));

        var matches = documents
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

    private FieldInfo BuildField(SchemaField field, SchemaPath path, ProcessorPipeline parentPipeline, BuildState state)
    {
        var processors = BindProcessors(field.Processors, path, state);
        return new FieldInfo
        {
            Name = field.Name,
            Path = path,
            Field = field,
            Pipeline = CreatePipeline(parentPipeline, processors),
        };
    }

    private GroupInfo BuildGroup(SchemaGroup group, SchemaPath path, ProcessorPipeline? parentPipeline, BuildState state)
    {
        var processors = BindProcessors(group.Processors, path, state);
        var pipeline = CreatePipeline(parentPipeline, processors);

        var children = new List<NodeInfo>(group.Children.Count);
        foreach (var child in group.Children)
        {
            if (BuildNode(child, path.Append(child.Name), pipeline, state) is { } childInfo)
                children.Add(childInfo);
        }

        GroupInfo info = group switch
        {
            SchemaRepeat repeat => new RepeatInfo
            {
                Name = group.Name,
                Path = path,
                Group = group,
                Children = children,
                Pipeline = pipeline,
                Count = BindValueSource(repeat.Count, path, state),
            },
            SchemaChoice choice => new ChoiceInfo
            {
                Name = group.Name,
                Path = path,
                Group = group,
                Children = children,
                Pipeline = pipeline,
                SelectedIndex = BindValueSource(choice.SelectedIndex, path, state),
            },
            _ => new GroupInfo
            {
                Name = group.Name,
                Path = path,
                Group = group,
                Children = children,
                Pipeline = pipeline,
            },
        };

        if (info is ChoiceInfo { SelectedIndex: int index } && (index < 0 || index >= children.Count))
        {
            state.Error(path, $"Choice index {index} is out of range (0..{children.Count - 1}).");
        }

        for (var i = 0; i < children.Count; i++)
        {
            children[i].Parent = info;
            children[i].Index = i;
        }

        return info;
    }

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
        var processor = processorProvider.CreateProcessor(
            new ProcessorKey(processorRef.Processor.Namespace, processorRef.Processor.Name));
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

        return new(processor, processorRef.Properties);
    }

    private ValueSource<int> BindValueSource(SchemaValueOrRef<int> source, string path, BuildState state)
        => source switch
        {
            int value => value,
            SchemaValueRef valueRef => BindValueRef(valueRef, path, state),
            _ => default,   // error already reported (or unresolved)
        };

    private static ValueSource<int> BindValueRef(SchemaValueRef valueRef, SchemaPath path, BuildState state)
    {
        if (string.IsNullOrWhiteSpace(valueRef.Reference))
        {
            state.Error(path, "A value reference cannot be empty.");
            return default;
        }

        if (valueRef.IsPublished)
        {
            var index = valueRef.Reference.IndexOf(SchemaValueRef.PublishedSeparator);
            return new PublishedValueKey(valueRef.Reference[..index], valueRef.Reference[(index + 1)..]);
        }

        var target = new SchemaPath(valueRef.Reference);
        state.PathReferences.Add((path, target));
        return PublishedValueKey.ForPath(target);
    }

    private static void ResolvePathReferences(GroupInfo root, BuildState state)
    {
        foreach (var (from, target) in state.PathReferences)
        {
            switch (Find(root, target))
            {
                case FieldInfo field:
                    field.PublishesValue = true;
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
            foreach (var child in group.Children)
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