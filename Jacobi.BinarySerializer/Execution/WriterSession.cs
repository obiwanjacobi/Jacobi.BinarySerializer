using System.Buffers;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Writes one value model to bytes by walking the <see cref="ExecutionPlan"/>:
/// value source -> Semantic -> Representation -> Layout -> (Stream at the root) -> output.
/// </summary>
/// <remarks>
/// Empty stages are short-circuited: no Semantic processors passes the value on as is, no Representation processors
/// encodes the value fixed-width (<see cref="DataTypeCodec"/>), no Layout processors appends the encoded bytes, and no
/// Stream processors writes straight to the output.
/// </remarks>
public sealed class WriterSession : SessionState
{
    private readonly ExecutionPlan _plan;
    private readonly IBufferWriter<byte> _output;
    private readonly IServiceProvider _services;
    private InstancePath _instance;

    private readonly ValueProcessorContext _valueContext;
    private readonly FieldProcessorContext _fieldContext;
    private readonly LayoutProcessorContext _layoutContext;
    private readonly StreamProcessorContext _streamContext;

    // where layout processors write: the output, or the root payload when the root has stream processors.
    private IBufferWriter<byte> _target;

    public WriterSession(ExecutionPlan plan, IBufferWriter<byte> output, IServiceProvider? services = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _target = output;
        _services = services ?? EmptyServiceProvider.Instance;

        _valueContext = new ValueProcessorContext(this) { Services = _services, Stage = PipelineStage.Semantic };
        _fieldContext = new FieldProcessorContext(this) { Services = _services, Stage = PipelineStage.Representation };
        _layoutContext = new LayoutProcessorContext(this) { Services = _services, Stage = PipelineStage.Layout };
        _streamContext = new StreamProcessorContext(this) { Services = _services, Stage = PipelineStage.Stream };
    }

    /// <summary>Writes the fields from the flat <paramref name="source"/> to the output.</summary>
    /// <param name="source">Asked for the value of each field that is written, in plan order.</param>
    /// <param name="range">Optional: only write these fields. The default is the whole plan.</param>
    public WriteResult Write(IFieldSource source, PlanRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Write(new FieldSourceAdapter(source), range);
    }

    /// <summary>Writes the values from <paramref name="source"/> to the output.</summary>
    /// <param name="range">Optional: only write these fields. The default is the whole plan.</param>
    /// <returns>Success, or the failing result of a processor.</returns>
    public WriteResult Write(IValueSource source, PlanRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = _plan.Root;
        var payload = root.Pipeline.StreamProcessors.Count > 0 ? new ArrayBufferWriter<byte>() : null;
        _target = payload ?? _output;

        var cursor = new PlanCursor<IValueSource>(root, range);
        while (true)
        {
            var step = cursor.Next();
            SetInstance(cursor.Instance);
            switch (step.Kind)
            {
                case CursorStepKind.EnterGroup:
                    {
                        var group = (GroupInfo)step.Node!;
                        if (step.Scope is null)
                        {
                            cursor.Enter(source);
                        }
                        else if (group is RepeatInfo repeat)
                        {
                            var count = Resolve(repeat.Count, repeat.Path);
                            CheckItemCount(repeat, step.Scope, count);
                            cursor.EnterRepeat(step.Scope, count);
                        }
                        else if (group is ChoiceInfo choice)
                        {
                            var index = Resolve(choice.SelectedIndex, choice.Path);
                            var choiceScope = step.Scope.EnterChoice(new ChoiceContext { Node = choice, Services = _services, Instance = _instance });
                            cursor.Enter(choiceScope, index);
                        }
                        else
                        {
                            cursor.Enter(step.Scope.EnterGroup(new GroupContext { Node = group, Services = _services, Instance = _instance }));
                        }
                        if (group is not RepeatInfo)
                        {
                            BeginLayout(group);
                        }
                        break;
                    }

                case CursorStepKind.Field:
                    {
                        var result = WriteField((FieldInfo)step.Node!, step.Scope!);
                        if (result != WriteResult.Success)
                        {
                            return result;
                        }
                        break;
                    }

                case CursorStepKind.ExitGroup:
                    {
                        var group = (GroupInfo)step.Node!;
                        if (group is not RepeatInfo)
                        {
                            EndLayout(group);
                        }
                        if (group == root && payload is not null)
                        {
                            return WriteStream(root, payload);
                        }
                        break;
                    }

                case CursorStepKind.EnterItem:
                    {
                        var repeat = (RepeatInfo)step.Node!;
                        var item = step.Scope!.EnterItem(new RepeatContext { Node = repeat, Services = _services, Instance = _instance.Append(step.Index) }, step.Index);
                        cursor.Enter(item);
                        SetInstance(cursor.Instance);
                        BeginLayout(repeat);
                        break;
                    }

                case CursorStepKind.ExitItem:
                    EndLayout((GroupInfo)step.Node!);
                    break;

                case CursorStepKind.Done:
                    return WriteResult.Success;
            }
        }
    }

    private void CheckItemCount(RepeatInfo repeat, IValueSource scope, int count)
    {
        int modelCount;
        try
        {
            modelCount = scope.GetCount(new RepeatContext { Node = repeat, Services = _services, Instance = _instance });
        }
        catch (NotSupportedException)
        {
            return;     // flat models cannot tell
        }

        if (modelCount != count)
        {
            throw new InvalidOperationException(
                $"'{repeat.Path}': the schema count is {count} but the value model has {modelCount} items.");
        }
    }

    private WriteResult WriteField(FieldInfo field, IValueSource scope)
    {
        var pipeline = field.Pipeline;

        if (!scope.TryGetField(new FieldContext { Node = field, Services = _services, Instance = _instance }, out var logical))
        {
            // TODO: derive values the model does not hold (lengths, counts, discriminators).
            throw new InvalidOperationException($"'{field.Path}': the value model has no value for the field.");
        }

        // Semantic: logical value transforms (chained)
        _valueContext.Field = field;
        foreach (var binding in pipeline.ValueProcessors)
        {
            Prepare(_valueContext, binding);
            logical = ((IValueProcessor)binding.Processor).Write(logical, _valueContext);
        }

        // Representation: logical -> encoded
        if (field.PublishesValue)
        {
            Publish(PublishedValueKey.ForPath(field.Path), logical.Value);
        }

        EncodedField encoded;
        switch (pipeline.FieldProcessors.Count)
        {
            case 0:
                if (!DataTypeCodec.TryEncode(field.Field.Type, logical.Value, out var bytes))
                {
                    throw new InvalidOperationException(
                        $"'{field.Path}': cannot encode value '{logical.Value ?? "null"}' as {field.Field.Type}.");
                }
                encoded = new EncodedField(logical.Name, typeof(byte[]), bytes, bytes.Length * 8);
                break;
            case 1:
                var fieldBinding = pipeline.FieldProcessors[0];
                _fieldContext.Field = field;
                Prepare(_fieldContext, fieldBinding);
                encoded = ((IFieldProcessor)fieldBinding.Processor).Write(logical, _fieldContext);
                break;
            default:
                // TODO: field processors cannot be chained (LogicalField -> EncodedField).
                throw new NotSupportedException($"'{field.Path}': multiple Representation processors are not supported.");
        }

        // Layout: encoded -> bytes in the target
        _layoutContext.Group = field.Parent!;
        _layoutContext.Field = field;
        switch (pipeline.LayoutProcessors.Count)
        {
            case 0:
                Prepare(_layoutContext, null);
                return ProcessorDefaults.DefaultLayoutProcessor.Write(_target, encoded, _layoutContext);
            case 1:
                var layoutBinding = pipeline.LayoutProcessors[0];
                Prepare(_layoutContext, layoutBinding);
                return ((ILayoutProcessor)layoutBinding.Processor).Write(_target, encoded, _layoutContext);
            default:
                // TODO: several layout processors writing into the same target.
                throw new NotSupportedException($"'{field.Path}': multiple Layout processors are not supported.");
        }
    }

    private void BeginLayout(GroupInfo group)
    {
        if (!OwnsLayout(group))
        {
            return;
        }

        _layoutContext.Group = group;
        _layoutContext.Field = null;
        foreach (var binding in group.Pipeline.LayoutProcessors)
        {
            Prepare(_layoutContext, binding);
            ((ILayoutProcessor)binding.Processor).BeginWrite(_target, _layoutContext);
        }
    }

    private void EndLayout(GroupInfo group)
    {
        if (!OwnsLayout(group))
        {
            return;
        }

        _layoutContext.Group = group;
        _layoutContext.Field = null;
        foreach (var binding in group.Pipeline.LayoutProcessors)
        {
            Prepare(_layoutContext, binding);
            ((ILayoutProcessor)binding.Processor).EndWrite(_target, _layoutContext);
        }
    }

    /// <summary>
    /// A group begins/ends the layout processors it declares itself. A group that inherits the layout stage
    /// (the same list instance as its parent's pipeline) continues in the parent's layout.
    /// </summary>
    internal static bool OwnsLayout(GroupInfo group)
        => group.Pipeline.LayoutProcessors.Count > 0
            && group.Pipeline.LayoutProcessors != group.Parent?.Pipeline.LayoutProcessors;

    /// <summary>Runs the stream stage over the buffered root payload (chained) into the output.</summary>
    private WriteResult WriteStream(GroupInfo root, ArrayBufferWriter<byte> payload)
    {
        _streamContext.Group = root;
        var input = payload;
        var processors = root.Pipeline.StreamProcessors;

        for (var i = 0; i < processors.Count; i++)
        {
            var binding = processors[i];
            var isLast = i == processors.Count - 1;
            var next = isLast ? null : new ArrayBufferWriter<byte>();

            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(input.WrittenMemory));
            Prepare(_streamContext, binding);
            var result = ((IStreamProcessor)binding.Processor).Write(ref reader, next ?? _output, _streamContext);
            if (result != WriteResult.Success)
            {
                if (result == WriteResult.NeedMoreSpace)
                {
                    throw new InvalidOperationException(
                        $"'{root.Path}': the stream processor '{binding.Processor.Name}' ({binding.Processor.Key}) needs more space in the output.");
                }
                return result;
            }

            if (next is not null)
            {
                input = next;
            }
        }

        return WriteResult.Success;
    }

    private void SetInstance(InstancePath instance)
    {
        _instance = instance;
        _valueContext.Instance = instance;
        _fieldContext.Instance = instance;
        _layoutContext.Instance = instance;
        _streamContext.Instance = instance;
    }

    private static void Prepare(ProcessorContext context, ProcessorBinding? binding)
    {
        // a null binding is an empty stage that was short-circuited: there is no processor-private state.
        context.Current = binding!;
        context.ProcessorProperties = binding?.Properties ?? [];
    }

    internal sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
