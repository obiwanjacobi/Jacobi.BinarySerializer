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
internal sealed class WriterSession : SessionState
{
    private readonly ExecutionPlan _plan;
    private readonly CountingBufferWriter _output;
    private readonly IServiceProvider _services;
    private readonly bool _valueFieldsUseModel;
    private InstancePath _instance;

    private readonly ValueProcessorContext _valueContext;
    private readonly FieldProcessorContext _fieldContext;
    private readonly LayoutProcessorContext _layoutContext;
    private readonly StreamProcessorContext _streamContext;

    // where layout processors write: the output, or the root payload when the root has stream processors.
    private IBufferWriter<byte> _target;
    private CountingBufferWriter _counter;
    private readonly Stack<long> _groupStarts = new();
    private readonly Dictionary<ProcessorBinding, long> _layoutPositions = [];
    private readonly Dictionary<ProcessorBinding, Stack<(long Position, int? BlockSize)>> _layoutGroupStarts = [];
    // per choice being written: the index of its alternative (-1 for the body of a choice with a memberNodeDef)
    private readonly Stack<int> _choiceIndexes = new();
    private readonly Stack<long> _sizeStarts = new();
    private readonly Stack<DeferredField> _deferred = new();

    // what a deferred field gets when its node is done: the size of a group, or the number of items of a repeat
    private enum DerivedValue { Size, Count }

    // a field that precedes the node its value is derived from: the output after it goes to a scratch buffer until the node is done
    private sealed record DeferredField(
        FieldInfo Field, IValueSource Scope, GroupInfo Node, DerivedValue Kind,
        CountingBufferWriter SavedCounter, IBufferWriter<byte> SavedTarget,
        ArrayBufferWriter<byte> Scratch, long Width);
    private readonly List<ArrayBufferWriter<byte>> _chainBuffers = [];

    /// <summary>
    /// <paramref name="valueFieldsUseModel"/>: also ask the source for fields that have a value (constant or ref). Default off.
    /// </summary>
    public WriterSession(ExecutionPlan plan, IBufferWriter<byte> output, IServiceProvider? services = null, bool valueFieldsUseModel = false)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _valueFieldsUseModel = valueFieldsUseModel;
        DataTypes = plan.DataTypes;
        _output = new CountingBufferWriter(output ?? throw new ArgumentNullException(nameof(output)));
        _counter = new CountingBufferWriter(output);
        _target = _counter;
        _services = services ?? EmptyServiceProvider.Instance;
        InitializeLogging(_services, "Write");
        _valueContext = new ValueProcessorContext(this) { Services = _services, Stage = PipelineStage.Semantic };
        _fieldContext = new FieldProcessorContext(this) { Services = _services, Stage = PipelineStage.Representation };
        _layoutContext = new LayoutProcessorContext(this) { Services = _services, Stage = PipelineStage.Layout };
        _streamContext = new StreamProcessorContext(this) { Services = _services, Stage = PipelineStage.Stream };
    }

    /// <summary>The physical bytes written to the output.</summary>
    public long BytesWritten => _output.Written;

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
    /// <exception cref="SerializationException">The write failed; it holds the location.</exception>
    public WriteResult Write(IValueSource source, PlanRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        try
        {
            return WriteCore(source, range);
        }
        catch (Exception exception) when (MustWrap(exception))
        {
            throw Wrap(exception);
        }
    }

    private WriteResult WriteCore(IValueSource source, PlanRange? range)
    {
        var root = _plan.Root;
        SetCurrent(root.Path, default, null);
        _plan.ValidateOpenChoices(range, EngineLogger);
        var payload = root.Pipeline.StreamProcessors.Count > 0 ? new ArrayBufferWriter<byte>() : null;
        _counter = new CountingBufferWriter((IBufferWriter<byte>?)payload ?? _output);
        _target = _counter;
        _groupStarts.Clear();
        _layoutPositions.Clear();
        _layoutGroupStarts.Clear();
        _layoutContext.BlockSizes.Clear();
        _sizeStarts.Clear();
        _deferred.Clear();
        EngineLogger.WriteStarted(root.Path.ToString());

        var cursor = new PlanCursor<IValueSource>(root, range, EngineLogger);
        while (true)
        {
            if (cursor.AtOpenRepeat && EndsOpenRepeat(cursor))
            {
                cursor.CloseRepeat();
            }
            var step = cursor.Next();
            SetInstance(step.Kind == CursorStepKind.ExitItem ? cursor.Instance.Append(step.Index) : cursor.Instance);
            if (step.Node is not null)
            {
                SetCurrent(step.Node.Path, StepInstance(step.Kind, step.Index), _counter.Written);
                EngineLogger.Navigating(step.Kind, step.Node.Path.ToString(), StepInstance(step.Kind, step.Index).ToLogString());
            }
            switch (step.Kind)
            {
                case CursorStepKind.EnterGroup:
                    {
                        var group = (GroupInfo)step.Node!;
                        if (step.Scope is null)
                        {
                            // the root: the model is the scope of the root node
                            switch (group)
                            {
                                case RepeatInfo { UntilEnd: true }:
                                    cursor.EnterOpenRepeat(source);
                                    break;
                                case RepeatInfo rootRepeat:
                                    cursor.EnterRepeat(source, Resolve(rootRepeat.Count, rootRepeat, _valueContext, _instance));
                                    break;
                                case ChoiceInfo { IsOpen: true } openRoot:
                                    throw EngineLogger.Fail($"'{openRoot.Path}': an open choice cannot be the root.", category: SerializationCategories.Schema);
                                case ChoiceInfo rootChoice:
                                    cursor.Enter(source, SelectChoice(rootChoice));
                                    break;
                                default:
                                    cursor.Enter(source);
                                    break;
                            }
                        }
                        else if (group is RepeatInfo openRepeat && (openRepeat.UntilEnd || IsDeferred(openRepeat)))
                        {
                            EnterOpenRepeat(openRepeat, step.Scope, cursor);
                        }
                        else if (group is RepeatInfo repeat)
                        {
                            var count = Resolve(repeat.Count, repeat, _valueContext, _instance);
                            EngineLogger.RepeatCount(repeat.Path.ToString(), count);
                            cursor.EnterRepeat(step.Scope.EnterRepeat(new RepeatContext { Node = repeat, Services = _services, Instance = _instance }, count), count);
                        }
                        else if (group is ChoiceInfo { IsOpen: true } openChoice)
                        {
                            var openSource = step.Scope as IOpenChoiceSource
                                ?? throw EngineLogger.Fail($"'{openChoice.Path}': the value model does not implement {nameof(IOpenChoiceSource)}, which an open choice requires.", category: SerializationCategories.Schema);
                            var selected = openSource.EnterOpenChoice(new OpenChoiceContext { Node = openChoice, Services = _services, Instance = _instance });
                            if (openChoice.MemberNodeDef is null)
                            {
                                var (index, memberRange) = SelectOpenChoiceMember(_plan, openChoice, selected.Member,
                                    selected.Begin, selected.BeginInstance, selected.End, selected.EndInstance, cursor.CurrentRange, cursor.RangeInstance());
                                _choiceIndexes.Push(index);
                                cursor.EnterOpenChoice(selected.Scope, index, memberRange);
                            }
                            else
                            {
                                var body = _plan.ResolveOpenChoiceBody(openChoice, selected.Member);
                                _choiceIndexes.Push(-1);
                                cursor.EnterOpenChoice(selected.Scope, body, _plan.CreateOpenChoiceRange(openChoice, body, selected.Begin, selected.BeginInstance, selected.End, selected.EndInstance));
                            }
                        }
                        else if (group is ChoiceInfo choice)
                        {
                            var index = SelectChoice(choice);
                            cursor.Enter(step.Scope.EnterChoice(new ChoiceContext { Node = choice, Services = _services, Instance = _instance }, index), index);
                        }
                        else
                        {
                            cursor.Enter(step.Scope.EnterGroup(new GroupContext { Node = group, Services = _services, Instance = _instance }));
                        }
                        if (group.HasByteSize)
                        {
                            _sizeStarts.Push(_counter.Written);
                        }
                        if (group is not RepeatInfo and not ChoiceInfo { MemberNodeDef: not null })
                        {
                            BeginLayout(group);
                        }
                        break;
                    }

                case CursorStepKind.Field:
                    {
                        var fieldNode = (FieldInfo)step.Node!;
                        EngineLogger.WritingField(fieldNode.Path.ToString(), _instance.ToLogString());
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
                        if (group is not RepeatInfo and not ChoiceInfo { MemberNodeDef: not null })
                        {
                            EndLayout(group);
                        }
                        switch (group)
                        {
                            case RepeatInfo exitRepeat:
                                step.Scope!.ExitRepeat(new RepeatContext { Node = exitRepeat, Services = _services, Instance = _instance }, step.Count);
                                break;
                            case ChoiceInfo exitChoice:
                                step.Scope!.ExitChoice(new ChoiceContext { Node = exitChoice, Services = _services, Instance = _instance }, _choiceIndexes.Pop());
                                break;
                            default:
                                step.Scope!.ExitGroup(new GroupContext { Node = group, Services = _services, Instance = _instance });
                                break;
                        }
                        long? size = group.HasByteSize ? _counter.Written - _sizeStarts.Pop() : null;
                        var (completed, sizeDerived) = CompleteDeferredFields(group, size, step.Count);
                        if (completed != WriteResult.Success)
                        {
                            return completed;
                        }
                        if (size is { } encoded && !sizeDerived)
                        {
                            var declared = Resolve(group.ByteSize, group.Path, _instance);
                            if (declared != encoded)
                            {
                                throw EngineLogger.Fail($"'{group.Path}': the declared size {declared} does not match the {encoded} encoded bytes.");
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
                        var item = step.Scope!.EnterItem(new RepeatContext { Node = repeat, Services = _services, Instance = _instance.Append(step.Index) }, step.Index, step.Count);
                        cursor.Enter(item);
                        SetInstance(cursor.Instance);
                        BeginLayout(repeat, step.Index, step.Count);
                        break;
                    }

                case CursorStepKind.ExitItem:
                    EndLayout((GroupInfo)step.Node!, step.Index, step.Count);
                    step.Scope!.ExitItem(new RepeatContext { Node = (RepeatInfo)step.Node!, Services = _services, Instance = _instance }, step.Index, step.Count);
                    break;

                case CursorStepKind.Done:
                    if (_deferred.Count > 0)
                    {
                        throw EngineLogger.Fail($"'{_deferred.Peek().Field.Path}': the field was deferred to derive its value, but '{_deferred.Peek().Node.Path}' was never completed.", category: SerializationCategories.Schema);
                    }
                    EngineLogger.WriteFinished(root.Path.ToString());
                    return WriteResult.Success;
            }
        }
    }

    private LogicalField DerivedField(FieldInfo field, long value)
        => new(field.Name, field.ValueType.ClrType, ToValueType(field, checked((int)value)));

    private bool IsDeferred(RepeatInfo repeat)
        => _deferred.Any(deferred => deferred.Kind == DerivedValue.Count && ReferenceEquals(deferred.Node, repeat));

    /// <summary>
    /// The field precedes the node its value is derived from (the group it measures, the repeat it counts): probe its
    /// encoded width, then redirect the output to a scratch buffer until the node ends.
    /// </summary>
    private WriteResult DeferField(FieldInfo field, IValueSource scope)
    {
        var (node, kind) = field.CountOf is { } countOf ? ((GroupInfo)countOf, DerivedValue.Count) : (field.ByteSizeOf!, DerivedValue.Size);
        var savedCounter = _counter;
        var savedTarget = _target;
        var probe = new ArrayBufferWriter<byte>();
        _counter = new CountingBufferWriter(probe, savedCounter.Written);
        _target = _counter;
        WriteResult result;
        try
        {
            var probeResult = WriteResult.Success;
            WithoutPublishing(() => probeResult = WriteField(field, scope, DerivedField(field, 0)));
            result = probeResult;
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
        _deferred.Push(new DeferredField(field, scope, node, kind, savedCounter, savedTarget, scratch, width));
        _counter = new CountingBufferWriter(scratch, savedCounter.Written + width);
        _target = _counter;
        return WriteResult.Success;
    }

    /// <summary>
    /// Writes the deferred fields whose value is derived from <paramref name="node"/>, now that it is done: its
    /// <paramref name="size"/>, its number of <paramref name="items"/>. They must be the innermost deferred fields.
    /// </summary>
    private (WriteResult Result, bool SizeDerived) CompleteDeferredFields(GroupInfo node, long? size, int items)
    {
        var sizeDerived = false;
        while (_deferred.TryPeek(out var top) && ReferenceEquals(top.Node, node))
        {
            sizeDerived |= top.Kind == DerivedValue.Size;
            var result = CompleteDeferredField(top.Kind == DerivedValue.Size ? size!.Value : items);
            if (result != WriteResult.Success)
            {
                return (result, sizeDerived);
            }
        }
        if (_deferred.FirstOrDefault(deferred => ReferenceEquals(deferred.Node, node)) is { } overlapping)
        {
            throw EngineLogger.Fail(
                $"'{overlapping.Field.Path}': its value is derived from '{node.Path}', which ends before '{_deferred.Peek().Node.Path}' that the field '{_deferred.Peek().Field.Path}' is derived from; " +
                "derived fields and their nodes must nest.", category: SerializationCategories.Schema);
        }
        return (WriteResult.Success, sizeDerived);
    }

    private WriteResult CompleteDeferredField(long value)
    {
        var deferred = _deferred.Pop();
        _counter = deferred.SavedCounter;
        _target = deferred.SavedTarget;

        var sizeBuffer = new ArrayBufferWriter<byte>();
        var outerCounter = _counter;
        var outerTarget = _target;
        _counter = new CountingBufferWriter(sizeBuffer, outerCounter.Written);
        _target = _counter;
        WriteResult result;
        try
        {
            result = WriteField(deferred.Field, deferred.Scope, DerivedField(deferred.Field, value));
        }
        finally
        {
            _counter = outerCounter;
            _target = outerTarget;
        }
        if (result != WriteResult.Success)
        {
            return result;
        }
        if (sizeBuffer.WrittenCount != deferred.Width && HasLayoutInside(deferred.Node))
        {
            throw EngineLogger.Fail(
                $"'{deferred.Field.Path}': the derived field is {sizeBuffer.WrittenCount} bytes but {deferred.Width} were assumed, " +
                $"and '{deferred.Node.Path}' contains layout processors whose result depends on that width.", category: SerializationCategories.Schema);
        }

        _target.Write(sizeBuffer.WrittenSpan);
        _target.Write(deferred.Scratch.WrittenSpan);
        return WriteResult.Success;
    }

    private static bool HasLayoutInside(GroupInfo group)
    {
        foreach (var member in group.Members)
        {
            if (member.Pipeline.LayoutProcessors.Count > 0 || (member is GroupInfo child && HasLayoutInside(child)))
            {
                return true;
            }
        }
        return false;
    }

    // the index of a choice with a selectedIndex, kept for its layout
    private int SelectChoice(ChoiceInfo choice)
    {
        var index = Resolve(choice.SelectedIndex, choice, _valueContext, _instance);
        EngineLogger.ChoiceSelected(choice.Path.ToString(), index);
        _choiceIndexes.Push(index);
        return index;
    }

    // enters a repeat without a count, or one whose count field is derived from the items written: the scope that
    // holds it (IOpenRepeatSource) gives the number of items, or the repeat is written item by item.
    private void EnterOpenRepeat(RepeatInfo repeat, IValueSource holder, PlanCursor<IValueSource> cursor)
    {
        var openSource = holder as IOpenRepeatSource
            ?? throw EngineLogger.Fail(repeat.UntilEnd
                ? $"'{repeat.Path}': the value model does not implement {nameof(IOpenRepeatSource)}, which a repeat without a count requires."
                : $"'{repeat.Path}': the value model has no value for its count field, which is then derived from the items written; that requires a value model that implements {nameof(IOpenRepeatSource)}.",
                category: SerializationCategories.Schema);
        var entered = openSource.EnterOpenRepeat(new RepeatContext { Node = repeat, Services = _services, Instance = _instance });
        EngineLogger.RepeatCount(repeat.Path.ToString(), entered.Count);
        if (entered.Count >= 0)
        {
            cursor.EnterRepeat(entered.Scope, entered.Count);
        }
        else
        {
            cursor.EnterOpenRepeat(entered.Scope);
        }
    }

    // asks the scope of the repeat (IOpenRepeatSource) or falls back to the EndOfData probe.
    private bool EndsOpenRepeat(PlanCursor<IValueSource> cursor)
    {
        var scope = cursor.CurrentScope;
        if (scope is IOpenRepeatSource openSource)
        {
            var context = new RepeatContext { Node = cursor.CurrentGroup!, Services = _services, Instance = cursor.Instance };
            switch (openSource.NextItem(context, cursor.NextItemIndex))
            {
                case OpenRepeatAction.Next:
                    return false;
                case OpenRepeatAction.End:
                    return true;
            }
        }
        return ProbeEndOfData(scope, cursor);
    }

    // probes the first field of the next item on the scope of the repeat.
    private bool ProbeEndOfData(IValueSource scope, PlanCursor<IValueSource> cursor)
    {
        GroupInfo node = cursor.CurrentGroup!;
        while (node is not RepeatInfo || ReferenceEquals(node, cursor.CurrentGroup))
        {
            var first = node.Members.Count > 0 ? node.Members[0] : null;
            if (first is FieldInfo field)
            {
                var context = new FieldContext { Node = field, Services = _services, Instance = cursor.Instance.Append(cursor.NextItemIndex) };
                return scope.GetField(context).Status == SourceStatus.EndOfData;
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

    private LogicalField? PrepareLogicalField(FieldInfo field, IValueSource scope, LogicalField? forced = null)
    {
        var pipeline = field.Pipeline;
        LogicalField logical;
        SourceResult sourced;

        if (forced is not null)
        {
            logical = forced;
        }
        else if (!_valueFieldsUseModel && field.HasExpectedValue && field.CountOf is null)
        {
            logical = new LogicalField(field.Name, field.ValueType.ClrType, ResolveExpected(field, _instance));
        }
        else if ((sourced = scope.GetField(new FieldContext { Node = field, Services = _services, Instance = _instance })).Status != SourceStatus.Value)
        {
            if (sourced.Status == SourceStatus.EndOfData)
            {
                throw EngineLogger.Fail($"'{field.Path}': the value source reported the end of data, which is only allowed on the first field of an item of a repeat without a count.", category: SerializationCategories.Schema);
            }
            if (field.CountOf is not null)
            {
                return null;    // derived from the items written (deferred)
            }
            else if (field.HasExpectedValue)
            {
                var expected = ResolveExpected(field, _instance);
                logical = new LogicalField(field.Name, field.ValueType.ClrType, expected);
            }
            else if (field.ByteLengthOf is { } lengthOf)
            {
                var bytesResult = scope.GetField(new FieldContext { Node = lengthOf, Services = _services, Instance = _instance });
                if (bytesResult.Status != SourceStatus.Value
                    || bytesResult.Value!.Value is not byte[] derivedBytes)
                {
                    throw EngineLogger.Fail($"'{field.Path}': cannot derive the length, the value model has no bytes value for '{lengthOf.Path}'.");
                }
                logical = new LogicalField(field.Name, field.ValueType.ClrType, ToValueType(field, derivedBytes.Length));
            }
            else if (field.ByteSizeOf is not null)
            {
                return null;
            }
            else
            {
                // TODO: derive other values the model does not hold (discriminators).
                throw EngineLogger.Fail($"'{field.Path}': the value model has no value for the field.");
            }
        }
        else
        {
            logical = ToValueType(field, sourced.Value!);
            if (field.HasExpectedValue)
            {
                CheckExpected(field, logical.Value, _instance);
            }
        }

        // the published value is the logical value (as on read)
        if (field.PublishesValue)
        {
            Publish(PublishedValueKey.ForPath(field.Path, _instance), logical.Value);
        }

        // Semantic: logical value transforms (chained)
        _valueContext.Field = field;
        foreach (var binding in pipeline.ValueProcessors)
        {
            Prepare(_valueContext, binding);
            logical = ((IValueProcessor)binding.Processor).Write(logical, _valueContext);
            Returned();
        }

        // Representation: logical -> encoded
        return logical;
    }

    private WriteResult WriteField(FieldInfo field, IValueSource scope, LogicalField? forced = null)
    {
        var logical = PrepareLogicalField(field, scope, forced);
        if (logical is null)
        {
            return DeferField(field, scope);
        }
        if (field.ByteOffset is not null)
        {
            return WriteResult.Success;
        }

        var pipeline = field.Pipeline;
        EncodedField encoded;
        if (field.LacksLength)
        {
            throw EngineLogger.Fail($"'{field.Path}': a {field.DataType} field needs a length: a byteLength (a constant, a reference, or -1: the rest of the size window or of the data) or a field processor that decides it.");
        }
        var fieldLength = field.HasByteLength ? Resolve(field.ByteLength, field.Path, _instance) : (int?)null;
        _fieldContext.FieldData.ByteLength = fieldLength;
        _layoutContext.FieldData.ByteLength = fieldLength;
        _fieldContext.FieldData.TakesRest = field.TakesRest;
        _layoutContext.FieldData.TakesRest = field.TakesRest;
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
                Returned();
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
        return WriteLayout(pipeline, encoded);
    }

    private WriteResult WriteLayout(ProcessorPipeline pipeline, EncodedField encoded)
    {
        switch (pipeline.LayoutProcessors.Count)
        {
            case 0:
                Prepare(_layoutContext, null);
                SetPosition();
                return DefaultProcessors.LayoutProcessor.Write(_target, encoded, _layoutContext);
            case 1:
                var layoutBinding = pipeline.LayoutProcessors[0];
                Prepare(_layoutContext, layoutBinding);
                SetPosition(layoutBinding);
                var start = _counter.Written;
                var singleResult = ((ILayoutProcessor)layoutBinding.Processor).Write(_target, encoded, _layoutContext);
                Returned();
                AdvanceLayoutPosition(layoutBinding, _counter.Written - start);
                return singleResult;
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
        Returned();
        for (var i = 1; i < chain.Count && result.Status == WriteResult.Success; i++)
        {
            Prepare(_fieldContext, chain[i]);
            result = ((IFieldWriter<EncodedField, EncodedField>)chain[i].Processor).Write(result.Value with { BitWidth = result.BitsWritten }, _fieldContext);
            Returned();
        }
        return result;
    }

    private void SetPosition()
    {
        _layoutContext.GroupData.RootPosition = _counter.Written;
        _layoutContext.GroupData.Position = _counter.Written - (_groupStarts.Count > 0 ? _groupStarts.Peek() : 0);
    }

    private void SetPosition(ProcessorBinding binding)
    {
        _layoutContext.GroupData.RootPosition = _counter.Written;
        var position = LayoutPosition(binding);
        _layoutContext.GroupData.Position = position - GroupStart(binding);
    }

    private long LayoutPosition(ProcessorBinding binding)
        => _layoutPositions.TryGetValue(binding, out var position) ? position : 0;

    private long GroupStart(ProcessorBinding binding)
        => _layoutGroupStarts.TryGetValue(binding, out var starts) && starts.Count > 0 ? starts.Peek().Position : 0;

    private void AdvanceLayoutPosition(ProcessorBinding binding, long count)
        => _layoutPositions[binding] = LayoutPosition(binding) + count;

    private void BeginLayoutScope(ProcessorBinding binding)
    {
        if (!_layoutGroupStarts.TryGetValue(binding, out var starts))
        {
            starts = new Stack<(long, int?)>();
            _layoutGroupStarts.Add(binding, starts);
        }
        starts.Push((LayoutPosition(binding), _layoutContext.BlockSizes.GetValueOrDefault(binding)));
        _layoutContext.BlockSizes[binding] = null;
    }

    private void EndLayoutScope(ProcessorBinding binding)
    {
        var scope = _layoutGroupStarts[binding].Pop();
        _layoutContext.BlockSizes[binding] = scope.BlockSize;
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

    /// <summary>The innermost processor creates bytes; each outer processor wraps them on the way to the target.</summary>
    private WriteResult WriteChained(IReadOnlyList<ProcessorBinding> chain, EncodedField encoded)
    {
        var buffers = RentBuffers(chain.Count - 1);
        var inner = chain.Count - 1;
        var innerOutput = buffers[inner - 1];
        Prepare(_layoutContext, chain[inner]);
        SetPosition(chain[inner]);
        var innerStart = innerOutput.WrittenCount;
        var result = ((ILayoutProcessor)chain[inner].Processor).Write(innerOutput, encoded, _layoutContext);
        Returned();
        AdvanceLayoutPosition(chain[inner], innerOutput.WrittenCount - innerStart);
        return result != WriteResult.Success ? result : ForwardWriteBlocks(chain, inner - 1, buffers,
            _layoutContext.BlockSizes.GetValueOrDefault(chain[inner]));
    }

    private WriteResult ForwardWriteBlocks(IReadOnlyList<ProcessorBinding> chain, int from,
        List<ArrayBufferWriter<byte>> buffers, int? blockSize)
    {
        var input = blockSize is { } size && buffers[from].WrittenCount > size
            ? buffers[from].WrittenSpan.ToArray()
            : null;
        _layoutContext.BlockByteLength = blockSize;
        try
        {
            if (input is null)
            {
                return ForwardWrite(chain, from, buffers);
            }
            buffers[from].Clear();
            var length = blockSize.GetValueOrDefault(input.Length);
            for (var offset = 0; offset < input.Length; offset += length)
            {
                var blockLength = Math.Min(length, input.Length - offset);
                _layoutContext.BlockByteLength = blockLength;
                buffers[from].Write(input.AsSpan(offset, blockLength));
                var result = ForwardWrite(chain, from, buffers);
                if (result != WriteResult.Success)
                {
                    return result;
                }
            }
            return WriteResult.Success;
        }
        finally
        {
            _layoutContext.BlockByteLength = null;
        }
    }

    /// <summary>Pushes bytes from an inner stage outward through stages starting at <paramref name="from"/>.</summary>
    private WriteResult ForwardWrite(IReadOnlyList<ProcessorBinding> chain, int from, List<ArrayBufferWriter<byte>> buffers)
    {
        for (var i = from; i >= 0; i--)
        {
            var input = buffers[i];
            if (input.WrittenCount == 0)
            {
                break;
            }

            Prepare(_layoutContext, chain[i]);
            SetPosition(chain[i]);
            if (i > 0)
            {
                buffers[i - 1].Clear();
            }
            var output = i == 0 ? _target : buffers[i - 1];
            var outputStart = i == 0 ? _counter.Written : buffers[i - 1].WrittenCount;
            var result = ((ILayoutWriter<ReadOnlySpan<byte>>)chain[i].Processor).Write(output, input.WrittenSpan, _layoutContext);
            Returned();
            var outputEnd = i == 0 ? _counter.Written : buffers[i - 1].WrittenCount;
            AdvanceLayoutPosition(chain[i], outputEnd - outputStart);
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
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        _layoutContext.FieldData.ByteLength = null;
        var result = ForwardWriteBlocks(chain, from, buffers, _layoutContext.BlockSizes.GetValueOrDefault(chain[from + 1]));
        if (result != WriteResult.Success)
        {
            throw EngineLogger.Fail($"'{group.Path}': a chained layout processor failed ({result}) while writing group-level output.");
        }
    }

    private int IndexOf(IReadOnlyList<ProcessorBinding> chain, ProcessorBinding binding)
    {
        var matchingKey = -1;
        for (var i = 0; i < chain.Count; i++)
        {
            if (ReferenceEquals(chain[i], binding))
            {
                return i;
            }
            if (matchingKey < 0 && chain[i].Processor.Key == binding.Processor.Key)
            {
                matchingKey = i;
            }
        }
        return matchingKey;
    }

    private void InvokeGroupHook(GroupInfo group, IReadOnlyList<ProcessorBinding> chain, ProcessorBinding binding, string hook)
    {
        var index = IndexOf(chain, binding);
        if (index < 0)
        {
            return;
        }
        var activeBinding = chain[index];

        var buffers = RentBuffers(chain.Count - 1);
        var output = index == 0 ? _target : buffers[index - 1];
        if (index > 0)
        {
            buffers[index - 1].Clear();
        }
        if (hook == "BeginWrite")
        {
            BeginLayoutScope(activeBinding);
        }
        Prepare(_layoutContext, activeBinding, hook);
        SetPosition(activeBinding);
        var outputStart = index == 0 ? _counter.Written : buffers[index - 1].WrittenCount;
        if (hook == "BeginWrite")
        {
            ((ILayoutProcessor)activeBinding.Processor).BeginWrite(output, _layoutContext);
        }
        else
        {
            ((ILayoutProcessor)activeBinding.Processor).EndWrite(output, _layoutContext);
        }
        Returned();
        var outputEnd = index == 0 ? _counter.Written : buffers[index - 1].WrittenCount;
        AdvanceLayoutPosition(activeBinding, outputEnd - outputStart);
        if (index > 0)
        {
            ForwardGroupWrite(group, chain, index - 1, buffers);
        }
        if (hook == "EndWrite")
        {
            EndLayoutScope(activeBinding);
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
        _layoutContext.FieldData.OpenWidthWindowBytes = DefaultLayoutProcessor.OpenWidthWindowBytes;
        _layoutContext.GroupData.ByteSize = null;
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        var chain = group.Pipeline.LayoutProcessors;
        foreach (var binding in group.Pipeline.OwnLayoutProcessors)
        {
            InvokeGroupHook(group, chain, binding, "BeginWrite");
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
        _layoutContext.FieldData.OpenWidthWindowBytes = DefaultLayoutProcessor.OpenWidthWindowBytes;
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        _layoutContext.GroupData.ByteSize = group is not RepeatInfo && group.HasByteSize && _sizeStarts.Count > 0
            ? checked((int)(_counter.Written - _sizeStarts.Peek()))
            : null;
        var chain = group.Pipeline.LayoutProcessors;
        for (var i = group.Pipeline.OwnLayoutProcessors.Count - 1; i >= 0; i--)
        {
            InvokeGroupHook(group, chain, group.Pipeline.OwnLayoutProcessors[i], "EndWrite");
        }
        _groupStarts.Pop();
    }

    /// <summary>
    /// A group begins/ends the layout processors it adds itself. The processors inherited from its parent
    /// are already active in the parent's layout scope.
    /// </summary>
    internal static bool OwnsLayout(GroupInfo group)
        => group.Members.Count > 0 && group.Pipeline.OwnLayoutProcessors.Count > 0
            && (group.Parent is null || group.Pipeline != group.Parent.Pipeline);

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
            var result = ((IStreamProcessor)binding.Processor).Write(ref reader, (IBufferWriter<byte>?)next ?? _output, _streamContext);
            Returned();
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

    private InstancePath StepInstance(CursorStepKind kind, int index)
        => kind == CursorStepKind.EnterItem ? _instance.Append(index) : _instance;

    private void SetInstance(InstancePath instance)
    {
        _instance = instance;
        _valueContext.Instance = instance;
        _fieldContext.Instance = instance;
        _layoutContext.Instance = instance;
        _streamContext.Instance = instance;
    }

    private void Prepare(ProcessorContext context, ProcessorBinding? binding, string direction = "write")
    {
        // a null binding is an empty stage that was short-circuited: there is no processor-private state.
        Calling(binding);
        context.Current = binding!;
        context.ProcessorProperties = binding?.Properties ?? [];
        if (binding is not null)
        {
            EngineLogger.ProcessorExecuting(context.Stage, binding.LogCategory, direction, context.NodePath, _instance.ToLogString());
        }
    }

    internal sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
