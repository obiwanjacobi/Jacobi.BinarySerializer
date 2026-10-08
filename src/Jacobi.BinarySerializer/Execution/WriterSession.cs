using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
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
    private CountingBufferWriter _counter;
    private readonly Stack<long> _groupStarts = new();
    private readonly Stack<int> _choiceIndexes = new();
    private readonly Stack<long> _sizeStarts = new();
    private readonly Stack<DeferredSize> _deferred = new();

    private sealed record DeferredSize(
        FieldInfo Field, IValueSource Scope, GroupInfo Group,
        CountingBufferWriter SavedCounter, IBufferWriter<byte> SavedTarget,
        ArrayBufferWriter<byte> Scratch, long Width);
    private readonly List<ArrayBufferWriter<byte>> _chainBuffers = [];

    public WriterSession(ExecutionPlan plan, IBufferWriter<byte> output, IServiceProvider? services = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        DataTypes = plan.DataTypes;
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _counter = new CountingBufferWriter(output);
        _target = _counter;
        _services = services ?? EmptyServiceProvider.Instance;
        InitializeLogging(_services, "Write");
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
        _counter = new CountingBufferWriter(payload ?? _output);
        _target = _counter;
        _groupStarts.Clear();
        _sizeStarts.Clear();
        _deferred.Clear();
        EngineLogger.WriteStarted(root.Path.ToString());

        var cursor = new PlanCursor<IValueSource>(root, range);
        while (true)
        {
            if (cursor.AtOpenRepeat && source is FieldSourceAdapter flat && ProbeEndOfData(flat, cursor))
            {
                cursor.CloseRepeat();
            }
            var step = cursor.Next();
            SetInstance(cursor.Instance);
            switch (step.Kind)
            {
                case CursorStepKind.EnterGroup:
                    {
                        var group = (GroupInfo)step.Node!;
                        if (step.Scope is null)
                        {
                            if (group is RepeatInfo { UntilEnd: true } openRoot)
                            {
                                if (source is FieldSourceAdapter)
                                {
                                    cursor.EnterOpenRepeat(source);
                                }
                                else
                                {
                                    cursor.EnterRepeat(source, source.GetCount(new RepeatContext { Node = openRoot, Services = _services, Instance = _instance }));
                                }
                            }
                            else if (group is RepeatInfo rootRepeat)
                            {
                                cursor.EnterRepeat(source, Resolve(rootRepeat.Count, rootRepeat, _valueContext, _instance));
                            }
                            else if (group is ChoiceInfo rootChoice)
                            {
                                var rootIndex = Resolve(rootChoice.SelectedIndex, rootChoice, _valueContext, _instance);
                                _choiceIndexes.Push(rootIndex);
                                cursor.Enter(source, rootIndex);
                            }
                            else
                            {
                                cursor.Enter(source);
                            }
                        }
                        else if (group is RepeatInfo { UntilEnd: true } flatRepeat && step.Scope is FieldSourceAdapter)
                        {
                            EngineLogger.RepeatCount(flatRepeat.Path.ToString(), -1);
                            cursor.EnterOpenRepeat(step.Scope);
                        }
                        else if (group is RepeatInfo { UntilEnd: true } openRepeat)
                        {
                            var count = step.Scope.GetCount(new RepeatContext { Node = openRepeat, Services = _services, Instance = _instance });
                            EngineLogger.RepeatCount(openRepeat.Path.ToString(), count);
                            cursor.EnterRepeat(step.Scope, count);
                        }
                        else if (group is RepeatInfo repeat)
                        {
                            var count = Resolve(repeat.Count, repeat, _valueContext, _instance);
                            CheckItemCount(repeat, step.Scope, count);
                            EngineLogger.RepeatCount(repeat.Path.ToString(), count);
                            cursor.EnterRepeat(step.Scope, count);
                        }
                        else if (group is ChoiceInfo choice)
                        {
                            var index = Resolve(choice.SelectedIndex, choice, _valueContext, _instance);
                            EngineLogger.ChoiceSelected(choice.Path.ToString(), index);
                            _choiceIndexes.Push(index);
                            var choiceScope = step.Scope.EnterChoice(new ChoiceContext { Node = choice, Services = _services, Instance = _instance });                            cursor.Enter(choiceScope, index);
                        }
                        else
                        {
                            cursor.Enter(step.Scope.EnterGroup(new GroupContext { Node = group, Services = _services, Instance = _instance }));
                        }
                        if (group.HasByteSize)
                        {
                            _sizeStarts.Push(_counter.Written);
                        }
                        if (group is not RepeatInfo)
                        {
                            BeginLayout(group);
                        }
                        break;
                    }

                case CursorStepKind.Field:
                    {
                        var fieldNode = (FieldInfo)step.Node!;
                        EngineLogger.WritingField(fieldNode.Path.ToString(), _instance.ToString());
                        var result = WriteField(fieldNode, step.Scope!);
                        if (result != WriteResult.Success)
                        {
                            EngineLogger.WriteStopped(fieldNode.Path.ToString(), result);
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
                        if (group is ChoiceInfo)
                        {
                            _choiceIndexes.Pop();
                        }
                        if (group.HasByteSize)
                        {
                            var size = _counter.Written - _sizeStarts.Pop();
                            if (_deferred.Count > 0 && ReferenceEquals(_deferred.Peek().Group, group))
                            {
                                var completed = CompleteDeferredSize(size);
                                if (completed != WriteResult.Success)
                                {
                                    return completed;
                                }
                            }
                            else
                            {
                                var declared = Resolve(group.ByteSize, group.Path, _instance);
                                if (declared != size)
                                {
                                    throw EngineLogger.Fail($"'{group.Path}': the declared size {declared} does not match the {size} encoded bytes.");
                                }
                            }
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
                        BeginLayout(repeat, step.Index, step.Count);
                        break;
                    }

                case CursorStepKind.ExitItem:
                    EndLayout((GroupInfo)step.Node!, step.Index, step.Count);
                    break;

                case CursorStepKind.Done:
                    if (_deferred.Count > 0)
                    {
                        throw EngineLogger.Fail($"'{_deferred.Peek().Field.Path}': the size field was deferred but its group was never completed.");
                    }
                    EngineLogger.WriteFinished(root.Path.ToString());
                    return WriteResult.Success;
            }
        }
    }

    private LogicalField DerivedSize(FieldInfo field, long size)
        => new(field.Name, field.DataType.ClrType, checked((int)size));

    /// <summary>
    /// The size field precedes the content it measures: probe its encoded width, then redirect the output to a scratch buffer until the group ends.
    /// </summary>
    private WriteResult DeferSizeField(FieldInfo field, IValueSource scope)
    {
        var savedCounter = _counter;
        var savedTarget = _target;
        var probe = new ArrayBufferWriter<byte>();
        _counter = new CountingBufferWriter(probe, savedCounter.Written);
        _target = _counter;
        WriteResult result;
        try
        {
            result = WriteField(field, scope, DerivedSize(field, 0));
        }
        finally
        {
            _counter = savedCounter;
            _target = savedTarget;
        }
        if (result != WriteResult.Success)
        {
            return result;
        }

        var width = probe.WrittenCount;
        var scratch = new ArrayBufferWriter<byte>();
        _deferred.Push(new DeferredSize(field, scope, field.ByteSizeOf!, savedCounter, savedTarget, scratch, width));
        _counter = new CountingBufferWriter(scratch, savedCounter.Written + width);
        _target = _counter;
        return WriteResult.Success;
    }

    private WriteResult CompleteDeferredSize(long size)
    {
        var deferred = _deferred.Pop();
        _counter = deferred.SavedCounter;
        _target = deferred.SavedTarget;

        var before = _counter.Written;
        var result = WriteField(deferred.Field, deferred.Scope, DerivedSize(deferred.Field, size));
        if (result != WriteResult.Success)
        {
            return result;
        }
        if (_counter.Written - before != deferred.Width)
        {
            // TODO: support variable-width derived size fields (e.g. varint).
            throw EngineLogger.Fail($"'{deferred.Field.Path}': the derived size field changed its encoded width.");
        }

        _target.Write(deferred.Scratch.WrittenSpan);
        return WriteResult.Success;
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
            throw EngineLogger.Fail(
                $"'{repeat.Path}': the schema count is {count} but the value model has {modelCount} items.");
        }
    }

    private bool ProbeEndOfData(FieldSourceAdapter source, PlanCursor<IValueSource> cursor)
    {
        GroupInfo node = cursor.CurrentGroup!;
        while (node is not RepeatInfo || ReferenceEquals(node, cursor.CurrentGroup))
        {
            var first = node.Members.Count > 0 ? node.Members[0] : null;
            if (first is FieldInfo field)
            {
                var context = new FieldContext { Node = field, Services = _services, Instance = cursor.Instance.Append(cursor.NextItemIndex) };
                return source.GetField(context).Status == SourceStatus.EndOfData;
            }
            if (first is not GroupInfo child || first is ChoiceInfo)
            {
                return false;
            }
            node = child;
            if (node is RepeatInfo)
            {
                return false;
            }
        }
        return false;
    }

    private WriteResult WriteField(FieldInfo field, IValueSource scope, LogicalField? forced = null)
    {
        var pipeline = field.Pipeline;
        LogicalField logical;
        SourceResult sourced;

        if (forced is not null)
        {
            logical = forced;
        }
        else if ((sourced = scope.GetField(new FieldContext { Node = field, Services = _services, Instance = _instance })).Status != SourceStatus.Value)
        {
            if (sourced.Status == SourceStatus.EndOfData)
            {
                throw EngineLogger.Fail($"'{field.Path}': the value source reported the end of data, which is only allowed on the first field of an item of a repeat without a count.");
            }
            if (field.CountOf is { } countOf)
            {
                var count = scope.GetCount(new RepeatContext { Node = countOf, Services = _services, Instance = _instance });
                logical = new LogicalField(field.Name, field.DataType.ClrType, count);
            }
            else if (field.HasExpectedValue)
            {
                var expected = ResolveExpected(field, _instance);
                logical = new LogicalField(field.Name, field.DataType.ClrType, expected);
            }
            else if (field.ByteLengthOf is { } lengthOf)
            {
                var bytesResult = scope.GetField(new FieldContext { Node = lengthOf, Services = _services, Instance = _instance });
                if (bytesResult.Status != SourceStatus.Value
                    || bytesResult.Value!.Value is not byte[] derivedBytes)
                {
                    throw EngineLogger.Fail($"'{field.Path}': cannot derive the length, the value model has no bytes value for '{lengthOf.Path}'.");
                }
                logical = new LogicalField(field.Name, field.DataType.ClrType, derivedBytes.Length);
            }
            else if (field.ByteSizeOf is not null)
            {
                return DeferSizeField(field, scope);
            }
            else
            {
                // TODO: derive other values the model does not hold (discriminators).
                throw EngineLogger.Fail($"'{field.Path}': the value model has no value for the field.");
            }
        }
        else
        {
            logical = sourced.Value!;
            if (field.HasExpectedValue)
            {
                CheckExpected(field, logical.Value, _instance);
            }
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
            Publish(PublishedValueKey.ForPath(field.Path, _instance), logical.Value);
        }

        EncodedField encoded;
        var fieldLength = field.HasByteLength ? Resolve(field.ByteLength, field.Path, _instance) : (int?)null;
        _fieldContext.FieldData.ByteLength = fieldLength;
        _layoutContext.FieldData.ByteLength = fieldLength;
        switch (pipeline.FieldProcessors.Count)
        {
            case 0:
                if (field.DataType.Encode is not { } encode || !encode(logical.Value, out var bytes))
                {
                    throw EngineLogger.Fail(
                        $"'{field.Path}': cannot encode value '{logical.Value ?? "null"}' as {field.DataType}.");
                }
                encoded = new EncodedField(logical.Name, typeof(byte[]), bytes, bytes.Length * 8);
                break;
            case 1:
                var fieldBinding = pipeline.FieldProcessors[0];
                _fieldContext.Field = field;
                Prepare(_fieldContext, fieldBinding);
                var single = ((IFieldProcessor)fieldBinding.Processor).Write(logical, _fieldContext);
                if (single.Status != WriteResult.Success)
                {
                    return single.Status;
                }
                encoded = single.Value with { BitWidth = single.BitsWritten };
                break;
            default:
                var chained = WriteChained(pipeline.FieldProcessors, logical, field);
                if (chained.Status != WriteResult.Success)
                {
                    return chained.Status;
                }
                encoded = chained.Value with { BitWidth = chained.BitsWritten };
                break;
        }

        // Length: a bytes field must be exactly as long as its declared length
        if (fieldLength is { } declaredLength && encoded.Value is byte[] lengthBytes)
        {
            if (declaredLength != lengthBytes.Length)
            {
                throw EngineLogger.Fail($"'{field.Path}': the length is {declaredLength} but the value has {lengthBytes.Length} bytes.");
            }
        }

        // Layout: encoded -> bytes in the target
        _layoutContext.Group = field.Parent!;
        _layoutContext.Field = field;
        switch (pipeline.LayoutProcessors.Count)
        {
            case 0:
                Prepare(_layoutContext, null);
                SetPosition();
                return ProcessorDefaults.DefaultLayoutProcessor.Write(_target, encoded, _layoutContext);
            case 1:
                var layoutBinding = pipeline.LayoutProcessors[0];
                Prepare(_layoutContext, layoutBinding);
                SetPosition();
                return ((ILayoutProcessor)layoutBinding.Processor).Write(_target, encoded, _layoutContext);
            default:
                return WriteChained(pipeline.LayoutProcessors, encoded);
        }
    }

    /// <summary>The head turns the logical value into an encoded one, each following stage transforms the encoded value.</summary>
    private FieldWriteResult<EncodedField> WriteChained(IReadOnlyList<ProcessorBinding> chain, LogicalField logical, FieldInfo field)
    {
        _fieldContext.Field = field;
        Prepare(_fieldContext, chain[0]);
        var result = ((IFieldProcessor)chain[0].Processor).Write(logical, _fieldContext);
        for (var i = 1; i < chain.Count && result.Status == WriteResult.Success; i++)
        {
            Prepare(_fieldContext, chain[i]);
            result = ((IFieldWriter<EncodedField, EncodedField>)chain[i].Processor).Write(result.Value with { BitWidth = result.BitsWritten }, _fieldContext);
        }
        return result;
    }

    private void SetPosition()
    {
        _layoutContext.GroupData.RootPosition = _counter.Written;
        _layoutContext.GroupData.Position = _counter.Written - (_groupStarts.Count > 0 ? _groupStarts.Peek() : 0);
    }

    private List<ArrayBufferWriter<byte>> RentBuffers(int count)
    {
        while (_chainBuffers.Count < count)
        {
            _chainBuffers.Add(new ArrayBufferWriter<byte>());
        }
        for (var i = 0; i < count; i++)
        {
            _chainBuffers[i].Clear();
        }
        return _chainBuffers;
    }

    /// <summary>The head writes into the first buffer, each following stage reads the previous buffer; the last one writes to the target.</summary>
    private WriteResult WriteChained(IReadOnlyList<ProcessorBinding> chain, EncodedField encoded)
    {
        var buffers = RentBuffers(chain.Count - 1);
        Prepare(_layoutContext, chain[0]);
        SetPosition();
        var result = ((ILayoutProcessor)chain[0].Processor).Write(buffers[0], encoded, _layoutContext);
        return result != WriteResult.Success ? result : ForwardWrite(chain, 1, buffers);
    }

    /// <summary>Pushes the bytes buffered by stage (from - 1) through the stages from..last.</summary>
    private WriteResult ForwardWrite(IReadOnlyList<ProcessorBinding> chain, int from, List<ArrayBufferWriter<byte>> buffers)
    {
        for (var j = from; j < chain.Count; j++)
        {
            var input = buffers[j - 1];
            if (input.WrittenCount == 0)
            {
                break;
            }

            Prepare(_layoutContext, chain[j]);
            SetPosition();
            var output = j == chain.Count - 1 ? _target : buffers[j];
            var result = ((ILayoutWriter<ReadOnlySpan<byte>>)chain[j].Processor).Write(output, input.WrittenSpan, _layoutContext);
            input.Clear();
            if (result != WriteResult.Success)
            {
                return result;
            }
        }

        return WriteResult.Success;
    }

    private void ForwardGroupWrite(GroupInfo group, IReadOnlyList<ProcessorBinding> chain, int from, List<ArrayBufferWriter<byte>> buffers)
    {
        // restore: the forward calls are group-level (no field)
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        var result = ForwardWrite(chain, from, buffers);
        if (result != WriteResult.Success)
        {
            throw EngineLogger.Fail($"'{group.Path}': a chained layout processor failed ({result}) while writing group-level output.");
        }
    }

    private void BeginLayout(GroupInfo group, int? itemIndex = null, int? itemCount = null)
    {
        if (!OwnsLayout(group))
        {
            return;
        }

        _groupStarts.Push(_counter.Written);
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        _layoutContext.FieldData.ByteLength = null;
        _layoutContext.GroupData.ByteSize = null;
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        var chain = group.Pipeline.LayoutProcessors;
        var buffers = RentBuffers(chain.Count - 1);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            Prepare(_layoutContext, chain[i]);
            SetPosition();
            ((ILayoutProcessor)chain[i].Processor).BeginWrite(i == chain.Count - 1 ? _target : buffers[i], _layoutContext);
            ForwardGroupWrite(group, chain, i + 1, buffers);
        }
    }

    private void EndLayout(GroupInfo group, int? itemIndex = null, int? itemCount = null)
    {
        if (!OwnsLayout(group))
        {
            return;
        }

        _layoutContext.Group = group;
        _layoutContext.Field = null;
        _layoutContext.FieldData.ByteLength = null;
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        _layoutContext.GroupData.ByteSize = group is not RepeatInfo && group.HasByteSize && _sizeStarts.Count > 0
            ? checked((int)(_counter.Written - _sizeStarts.Peek()))
            : null;
        var chain = group.Pipeline.LayoutProcessors;
        var buffers = RentBuffers(chain.Count - 1);
        for (var i = 0; i < chain.Count; i++)
        {
            Prepare(_layoutContext, chain[i]);
            SetPosition();
            ((ILayoutProcessor)chain[i].Processor).EndWrite(i == chain.Count - 1 ? _target : buffers[i], _layoutContext);
            ForwardGroupWrite(group, chain, i + 1, buffers);
        }
        _groupStarts.Pop();
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
                    throw EngineLogger.Fail(
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
