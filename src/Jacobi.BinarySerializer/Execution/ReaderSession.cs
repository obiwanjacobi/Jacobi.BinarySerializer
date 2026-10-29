using System.Buffers;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Execution;

/// <summary>
/// Reads bytes into a value model by walking the <see cref="ExecutionPlan"/>:
/// input -> (Stream at the root) -> Layout -> Representation -> Semantic -> value sink.
/// </summary>
/// <remarks>
/// The mirror of <see cref="WriterSession"/>; empty stages are short-circuited in the same way.
/// When the input does not (yet) hold a complete value, <see cref="ReadResult.NeedMoreData"/> is returned;
/// the caller then reads again with more data (from the start of the message).
/// </remarks>
internal sealed class ReaderSession : SessionState
{
    private readonly ExecutionPlan _plan;
    private readonly IServiceProvider _services;
    private readonly bool _valueFieldsUseModel;
    private InstancePath _instance;
    private (GroupInfo Group, IValueSink Scope)? _openBody;
    private readonly Stack<long> _groupStarts = new();
    private readonly Dictionary<ProcessorBinding, long> _layoutPositions = [];
    private readonly Dictionary<ProcessorBinding, Stack<(long Position, int? BlockSize)>> _layoutGroupStarts = [];
    private readonly Dictionary<ProcessorBinding, LayoutInput> _layoutInputs = [];

    private sealed class LayoutInput
    {
        public ArrayBufferWriter<byte> Buffer { get; } = new();
        public int Consumed { get; set; }
        public long RootPosition { get; set; }
        public int Remaining => Buffer.WrittenCount - Consumed;

        public SequenceReader<byte> Reader()
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(Buffer.WrittenMemory));
            reader.Advance(Consumed);
            return reader;
        }
    }

    private enum LayoutOperation { Field, Bytes, Begin, End }

    private sealed class LayoutFrame(int index, LayoutOperation operation, int? byteLength, int? blockSize, bool continuation)
    {
        public int Index { get; } = index;
        public LayoutOperation Operation { get; } = operation;
        public int? ByteLength { get; } = byteLength;
        public int? BlockSize { get; } = blockSize;
        public bool Continuation { get; } = continuation;
        public int Window { get; set; }
        public int ParentCalls { get; set; }
    }
    private readonly Stack<int> _choiceIndexes = new();
    private readonly Stack<(GroupInfo Group, long End, int AncestorTail)> _windows = new();
    private readonly Dictionary<ProcessorBinding, Stack<int>> _layoutTails = [];
    private readonly Dictionary<GroupInfo, int> _minItemBytes = new();
    private int _tailTotal;
    private long _transportConsumed;

    /// <summary>
    /// The physical bytes consumed from the input by the last read (the transport bytes when the root has stream processors).
    /// </summary>
    public long BytesRead { get; private set; }

    private readonly ValueProcessorContext _valueContext;
    private readonly FieldProcessorContext _fieldContext;
    private readonly LayoutProcessorContext _layoutContext;
    private readonly StreamProcessorContext _streamContext;

    /// <summary>
    /// <paramref name="valueFieldsUseModel"/>: also report fields that have a value (constant or ref) to the sink. Default off.
    /// </summary>
    public ReaderSession(ExecutionPlan plan, IServiceProvider? services = null, bool valueFieldsUseModel = false)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _valueFieldsUseModel = valueFieldsUseModel;
        DataTypes = plan.DataTypes;
        _services = services ?? WriterSession.EmptyServiceProvider.Instance;
        InitializeLogging(_services, "Read");

        _valueContext = new ValueProcessorContext(this) { Services = _services, Stage = PipelineStage.Semantic };
        _fieldContext = new FieldProcessorContext(this) { Services = _services, Stage = PipelineStage.Representation };
        _layoutContext = new LayoutProcessorContext(this) { Services = _services, Stage = PipelineStage.Layout };
        _streamContext = new StreamProcessorContext(this) { Services = _services, Stage = PipelineStage.Stream };
    }

    /// <summary>Reads the fields from <paramref name="input"/> into the flat <paramref name="sink"/>.</summary>
    /// <param name="sink">Receives each field that is read, in plan order.</param>
    /// <param name="range">Optional: only read these fields. The default is the whole plan.</param>
    public ReadResult Read(ReadOnlySequence<byte> input, IFieldSink sink, PlanRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return Read(input, new FieldSinkAdapter(sink), range);
    }

    /// <summary>Reads the values from <paramref name="input"/> into <paramref name="sink"/>.</summary>
    /// <param name="range">Optional: only read these fields. The default is the whole plan.</param>
    public ReadResult Read(ReadOnlySequence<byte> input, IValueSink sink, PlanRange? range = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var root = _plan.Root;
        var payload = input;
        _plan.ValidateOpenChoices(range, EngineLogger);
        BytesRead = 0;
        _transportConsumed = 0;
        if (root.Pipeline.StreamProcessors.Count > 0)
        {
            var result = ReadStream(root, input, out var buffer);
            if (result != ReadResult.Success)
            {
                return result;
            }
            payload = buffer;
        }

        var reader = new SequenceReader<byte>(payload);
        _groupStarts.Clear();
        _layoutPositions.Clear();
        _layoutGroupStarts.Clear();
        _layoutContext.BufferedInputs.Clear();
        _layoutContext.BlockSizes.Clear();
        _layoutInputs.Clear();
        _layoutContext.InputByteLength = null;
        _layoutContext.IsReadContinuation = false;
        _windows.Clear();
        _layoutTails.Clear();
        _tailTotal = 0;
        EngineLogger.ReadStarted(root.Path.ToString());
        var cursor = new PlanCursor<IValueSink>(root, range, EngineLogger);
        while (true)
        {
            if (cursor.AtOpenRepeat && EndsOpenRepeat(ref reader, cursor))
            {
                cursor.CloseRepeat();
            }
            var step = cursor.Next();
            SetInstance(step.Kind == CursorStepKind.ExitItem ? cursor.Instance.Append(step.Index) : cursor.Instance);
            if (step.Node is not null)
            {
                EngineLogger.Navigating(step.Kind, step.Node.Path.ToString(), StepInstance(step.Kind, step.Index).ToLogString());
            }
            switch (step.Kind)
            {
                case CursorStepKind.EnterGroup:
                    {
                        var group = (GroupInfo)step.Node!;
                        if (step.Scope is null)
                        {
                            if (group is RepeatInfo { UntilEnd: true })
                            {
                                cursor.EnterOpenRepeat(sink);
                            }
                            else if (group is RepeatInfo rootRepeat)
                            {
                                cursor.EnterRepeat(sink, Resolve(rootRepeat.Count, rootRepeat, _valueContext, _instance));
                            }
                            else if (group is ChoiceInfo { IsOpen: true } openRoot)
                            {
                                throw EngineLogger.Fail($"'{openRoot.Path}': an open choice cannot be the root.");
                            }
                            else if (group is ChoiceInfo rootChoice)
                            {
                                var rootIndex = Resolve(rootChoice.SelectedIndex, rootChoice, _valueContext, _instance);
                                _choiceIndexes.Push(rootIndex);
                                cursor.Enter(sink, rootIndex);
                            }
                            else
                            {
                                cursor.Enter(sink);
                            }
                        }
                        else if (group is RepeatInfo { UntilEnd: true } openRepeat)
                        {
                            EngineLogger.RepeatCount(openRepeat.Path.ToString(), -1);
                            cursor.EnterOpenRepeat(step.Scope);
                        }
                        else if (group is RepeatInfo repeat)
                        {
                            var count = Resolve(repeat.Count, repeat, _valueContext, _instance);
                            EngineLogger.RepeatCount(repeat.Path.ToString(), count);
                            cursor.EnterRepeat(step.Scope, count);
                        }
                        else if (_openBody is { } openBody && ReferenceEquals(openBody.Group, group))
                        {
                            _openBody = null;
                            cursor.Enter(openBody.Scope);
                        }
                        else if (group is ChoiceInfo { IsOpen: true } openChoice)
                        {
                            var openSink = step.Scope as IOpenChoiceSink
                                ?? throw EngineLogger.Fail($"'{openChoice.Path}': the value model does not implement {nameof(IOpenChoiceSink)}, which an open choice requires.");
                            var selected = openSink.EnterOpenChoice(new OpenChoiceContext { Node = openChoice, Services = _services, Instance = _instance });
                            var body = _plan.ResolveOpenChoiceBody(openChoice, selected.Group, selected.FromTarget);
                            _openBody = (body, selected.Scope);
                            cursor.EnterOpenChoice(step.Scope, body, _plan.CreateOpenChoiceRange(openChoice, body, selected.FromTarget, selected.FromInstance, selected.ToTarget, selected.ToInstance));
                        }
                        else if (group is ChoiceInfo choice)
                        {
                            var index = Resolve(choice.SelectedIndex, choice, _valueContext, _instance);
                            EngineLogger.ChoiceSelected(choice.Path.ToString(), index);
                            _choiceIndexes.Push(index);
                            var choiceScope = step.Scope.EnterChoice(new ChoiceContext { Node = choice, Services = _services, Instance = _instance }, index); cursor.Enter(choiceScope, index);
                        }
                        else
                        {
                            cursor.Enter(step.Scope.EnterGroup(new GroupContext { Node = group, Services = _services, Instance = _instance }));
                        }
                        if (group.HasByteSize)
                        {
                            var size = Resolve(group.ByteSize, group.Path, _instance);
                            if (size < 0)
                            {
                                throw EngineLogger.Fail($"'{group.Path}': the size {size} cannot be negative.");
                            }
                            if (reader.Remaining < size)
                            {
                                EngineLogger.ReadStopped(group.Path.ToString(), ReadResult.NeedMoreData);
                                return ReadResult.NeedMoreData;
                            }
                            _windows.Push((group, reader.Consumed + size, _tailTotal));
                        }
                        if (group is not RepeatInfo and not ChoiceInfo { IsOpen: true })
                        {
                            var status = BeginLayout(group, ref reader);
                            if (status != ReadResult.Success)
                            {
                                return status;
                            }
                        }
                        break;
                    }

                case CursorStepKind.Field:
                    {
                        var fieldNode = (FieldInfo)step.Node!;
                        EngineLogger.ReadingField(fieldNode.Path.ToString(), _instance.ToLogString());
                        var result = fieldNode.ByteOffset is { } byteOffset
                            ? ReadVirtualField(fieldNode, byteOffset, step.Scope!, ref reader)
                            : ReadField(fieldNode, step.Scope!, ref reader);
                        if (result != ReadResult.Success)
                        {
                            EngineLogger.ReadStopped(fieldNode.Path.ToString(), result);
                            return result;
                        }
                        break;
                    }

                case CursorStepKind.EnterItem:
                    {
                        var repeat = (RepeatInfo)step.Node!;
                        var item = step.Scope!.EnterItem(
                            new RepeatContext { Node = repeat, Services = _services, Instance = _instance.Append(step.Index) }, step.Index, step.Count);
                        cursor.Enter(item);
                        SetInstance(cursor.Instance);
                        var status = BeginLayout(repeat, ref reader, step.Index, step.Count);
                        if (status != ReadResult.Success)
                        {
                            return status;
                        }
                        break;
                    }

                case CursorStepKind.ExitItem:
                    var endStatus = EndLayout((GroupInfo)step.Node!, ref reader, step.Index, step.Count);
                    if (endStatus != ReadResult.Success)
                    {
                        return endStatus;
                    }
                    step.Scope!.ExitItem(
                        new RepeatContext { Node = (RepeatInfo)step.Node!, Services = _services, Instance = _instance }, step.Index, step.Count);
                    break;

                case CursorStepKind.ExitGroup:
                    if (step.Node is not RepeatInfo and not ChoiceInfo { IsOpen: true })
                    {
                        var status = EndLayout((GroupInfo)step.Node!, ref reader);
                        if (status != ReadResult.Success)
                        {
                            return status;
                        }
                        if (step.Node is ChoiceInfo exitChoice)
                        {
                            step.Scope!.ExitChoice(
                                new ChoiceContext { Node = exitChoice, Services = _services, Instance = _instance }, _choiceIndexes.Peek());
                        }
                        else
                        {
                            step.Scope!.ExitGroup(
                                new GroupContext { Node = (GroupInfo)step.Node!, Services = _services, Instance = _instance });
                        }
                    }
                    if (step.Node is ChoiceInfo { IsOpen: false })
                    {
                        _choiceIndexes.Pop();
                    }
                    if (step.Node is GroupInfo { HasByteSize: true } sized && _windows.Count > 0 && ReferenceEquals(_windows.Peek().Group, sized))
                    {
                        var end = _windows.Pop().End;
                        if (reader.Consumed != end)
                        {
                            throw EngineLogger.Fail(
                                $"'{sized.Path}': the content ended at position {reader.Consumed} but the size requires {end}.");
                        }
                    }
                    break;

                case CursorStepKind.Done:
                    BytesRead = root.Pipeline.StreamProcessors.Count > 0 ? _transportConsumed : reader.Consumed;
                    EngineLogger.ReadFinished(root.Path.ToString());
                    return ReadResult.Success;
            }
        }
    }

    // asks the scope that holds the repeat (IOpenRepeatSink) or falls back to the engine's own rule.
    private bool EndsOpenRepeat(ref SequenceReader<byte> reader, PlanCursor<IValueSink> cursor)
    {
        var repeat = (RepeatInfo)cursor.CurrentGroup!;
        var atEnd = AtOpenRepeatEnd(ref reader, repeat);
        if (cursor.CurrentScope is not IOpenRepeatSink openSink)
        {
            if (!repeat.EndsAtInputEnd)
            {
                throw EngineLogger.Fail($"'{repeat.Path}': a repeat without a count that is followed by other nodes requires a value model that implements {nameof(IOpenRepeatSink)}.");
            }
            return atEnd;
        }

        var remaining = Math.Max(0, ReadableEnd(ref reader) - reader.Consumed);
        var context = new OpenRepeatReadContext
        {
            Node = repeat,
            Services = _services,
            Instance = cursor.Instance,
            Index = cursor.NextItemIndex,
            Remaining = remaining,
            AtDefaultEnd = atEnd,
            Input = HasBufferedLayoutInput(repeat) ? null : reader.UnreadSequence.Slice(0, remaining),
        };
        return openSink.NextItem(context) switch
        {
            OpenRepeatAction.Next => false,
            OpenRepeatAction.End => true,
            _ when !repeat.EndsAtInputEnd => throw EngineLogger.Fail(
                $"'{repeat.Path}': the value model answered {nameof(OpenRepeatAction.Default)} for a repeat without a count that is followed by other nodes; it must answer {nameof(OpenRepeatAction.Next)} or {nameof(OpenRepeatAction.End)}."),
            _ => atEnd,
        };
    }

    private bool HasBufferedLayoutInput(RepeatInfo repeat)
        => repeat.Pipeline.LayoutProcessors.Any(binding => _layoutContext.BufferedInputs.GetValueOrDefault(binding)
            || _layoutInputs.TryGetValue(binding, out var input) && input.Remaining > 0);

    private bool AtOpenRepeatEnd(ref SequenceReader<byte> reader, RepeatInfo repeat)
    {
        if (HasBufferedLayoutInput(repeat))
        {
            return false;
        }
        if (_windows.Count > 0 || repeat.Members.Any(UsesLayout))
        {
            return reader.Consumed >= ReadableEnd(ref reader);
        }
        if (!_minItemBytes.TryGetValue(repeat, out var min))
        {
            min = repeat.Members.Sum(MinBytes);
            _minItemBytes[repeat] = min;
        }
        return reader.Remaining - _tailTotal < Math.Max(min, 1);
    }

    private static bool UsesLayout(NodeInfo node)
        => node.Pipeline.LayoutProcessors.Count > 0 || node is GroupInfo group && group.Members.Any(UsesLayout);

    private long ReadableEnd(ref SequenceReader<byte> reader)
    {
        var end = reader.Sequence.Length - _tailTotal;
        foreach (var window in _windows)
        {
            end = Math.Min(end, window.End - (_tailTotal - window.AncestorTail));
        }
        return end;
    }

    /// <summary>
    /// The lower bound of the encoded size of a node (0 when unknown).
    /// </summary>
    private static int MinBytes(NodeInfo node)
        => node switch
        {
            FieldInfo { ByteOffset: not null } => 0,
            FieldInfo { ByteLength: int length } => length,
            FieldInfo { HasByteLength: true } => 0,
            FieldInfo field => field.Pipeline.FieldProcessors.Count == 0 ? field.DataType.FixedSize ?? 0 : 0,
            GroupInfo { ByteSize: int size } => size,
            ChoiceInfo => 0,
            RepeatInfo { Count: int count } repeat => checked(count * repeat.Members.Sum(MinBytes)),
            RepeatInfo => 0,
            GroupInfo group => group.Members.Sum(MinBytes),
            _ => 0
        };

    /// <summary>
    /// The number of bytes (physical) a field with a length takes: its length, or the rest of the enclosing size window for a data type that takes the rest. Null for other fields.
    /// </summary>
    private int? BytesLength(FieldInfo field, ref SequenceReader<byte> reader)
    {
        if (!field.DataType.SupportsLength)
        {
            return null;
        }

        if (field.HasByteLength)
        {
            var length = Resolve(field.ByteLength, field.Path, _instance);
            if (length < 0)
            {
                throw EngineLogger.Fail($"'{field.Path}': the length is negative ({length}).");
            }
            return length;
        }

        // validated at plan build: a length-less rest-of-window field has an enclosing window.
        return field.DataType.TakesRestOfWindow
            ? checked((int)(_windows.Peek().End - reader.Consumed))
            : null;
    }

    /// <summary>
    /// Reads a field at a byte offset from the current position and restores the position afterwards. The field is not given to the model.
    /// </summary>
    private ReadResult ReadVirtualField(FieldInfo field, int byteOffset, IValueSink scope, ref SequenceReader<byte> reader)
    {
        var start = reader.Consumed;
        var target = start + byteOffset;
        if (target < 0)
        {
            throw EngineLogger.Fail($"'{field.Path}': the byte offset {byteOffset} lies before the start of the input.");
        }
        if (target > reader.Consumed + reader.Remaining)
        {
            return ReadResult.NeedMoreData;
        }

        var lookahead = reader;
        if (byteOffset >= 0)
        {
            lookahead.Advance(byteOffset);
        }
        else
        {
            lookahead.Rewind(-byteOffset);
        }
        var saved = field.Pipeline.LayoutProcessors.Select(binding =>
            (Binding: binding, Position: LayoutPosition(binding), Input: _layoutInputs.GetValueOrDefault(binding))).ToArray();
        foreach (var entry in saved)
        {
            _layoutInputs.Remove(entry.Binding);
        }
        try
        {
            return ReadField(field, scope, ref lookahead);
        }
        finally
        {
            foreach (var entry in saved)
            {
                _layoutPositions[entry.Binding] = entry.Position;
                if (entry.Input is null)
                {
                    _layoutInputs.Remove(entry.Binding);
                }
                else
                {
                    _layoutInputs[entry.Binding] = entry.Input;
                }
            }
        }
    }

    private ReadResult ReadField(FieldInfo field, IValueSink scope, ref SequenceReader<byte> reader)
    {
        var pipeline = field.Pipeline;
        _layoutContext.Group = field.Parent!;
        _layoutContext.Field = field;
        _layoutContext.FieldData.OpenWidthWindowBytes = DefaultLayoutProcessor.OpenWidthWindowBytes;
        var fieldLength = BytesLength(field, ref reader);
        _layoutContext.FieldData.ByteLength = fieldLength;
        _fieldContext.FieldData.ByteLength = fieldLength;
        _fieldContext.Field = field;
        var open = pipeline.FieldProcessors.Count > 0 && fieldLength is null;
        var encodedBytes = new ArrayBufferWriter<byte>();
        while (true)
        {
            var layoutRead = ReadLayout(pipeline, ref reader, open ? 1 : fieldLength, encodedBytes.WrittenCount > 0);
            if (layoutRead.Status != ReadResult.Success)
            {
                var inputEnd = reader.Sequence.Length;
                if (layoutRead.Status == ReadResult.NeedMoreData && _windows.Any(window => window.End <= inputEnd))
                {
                    throw EngineLogger.Fail($"'{field.Path}': the field does not fit in its enclosing byte-size window.");
                }
                return layoutRead.Status;
            }
            var encoded = layoutRead.Value
                ?? throw EngineLogger.Fail($"'{field.Path}': the layout succeeded without an encoded field.");
            if (pipeline.FieldProcessors.Count == 0)
            {
                if (encoded.Value is not byte[] bytes || field.DataType.Decode is not { } decode || !decode(bytes, out var value))
                {
                    throw EngineLogger.Fail($"'{field.Path}': cannot decode the bytes as {field.DataType}.");
                }
                CompleteLogicalField(field, new LogicalField(encoded.Name, field.DataType.ClrType, value), scope);
                return ReadResult.Success;
            }
            if (open && encoded.Value is byte[] chunk)
            {
                if (chunk.Length == 0)
                {
                    throw EngineLogger.Fail($"'{field.Path}': the layout produced no encoded bytes for an incomplete field.");
                }
                encodedBytes.Write(chunk);
                encoded = new EncodedField(encoded.Name, typeof(byte[]), encodedBytes.WrittenSpan.ToArray(), encodedBytes.WrittenCount * 8);
            }
            var fieldResult = pipeline.FieldProcessors.Count == 1
                ? ReadSingle(pipeline.FieldProcessors[0], encoded)
                : ReadChained(pipeline.FieldProcessors, encoded, field);
            if (fieldResult.Status == ReadResult.NeedMoreData && open)
            {
                continue;
            }
            if (fieldResult.Status != ReadResult.Success)
            {
                return fieldResult.Status;
            }
            if (fieldResult.BitsConsumed < 0 || fieldResult.BitsConsumed > encoded.BitWidth || fieldResult.BitsConsumed % 8 != 0)
            {
                throw EngineLogger.Fail($"'{field.Path}': the field processor consumed {fieldResult.BitsConsumed} bits of the {encoded.BitWidth} bits provided.");
            }
            if (fieldResult.BitsConsumed != encoded.BitWidth)
            {
                throw EngineLogger.Fail($"'{field.Path}': the field processor left encoded bytes outside its field boundary.");
            }
            CompleteLogicalField(field, fieldResult.Value, scope);
            return ReadResult.Success;
        }
    }

    private void CompleteLogicalField(FieldInfo field, LogicalField logical, IValueSink scope)
    {
        // Semantic: reverse order of writing
        _valueContext.Field = field;
        var valueProcessors = field.Pipeline.ValueProcessors;
        for (var i = valueProcessors.Count - 1; i >= 0; i--)
        {
            Prepare(_valueContext, valueProcessors[i]);
            logical = ((IValueProcessor)valueProcessors[i].Processor).Read(logical, _valueContext);
        }
        logical = ToValueType(field, logical);

        if (field.HasExpectedValue)
        {
            CheckExpected(field, logical.Value, _instance);
        }

        if (field.PublishesValue)
        {
            Publish(PublishedValueKey.ForPath(field.Path, _instance), logical.Value);
        }

        if (_valueFieldsUseModel || !field.HasExpectedValue)
        {
            scope.SetField(new FieldContext { Node = field, Services = _services, Instance = _instance }, logical);
        }
    }

    private LayoutReadResult<EncodedField> ReadLayout(ProcessorPipeline pipeline, ref SequenceReader<byte> reader,
        int? byteLength = null, bool continuation = false)
    {
        var result = RunLayout(pipeline.LayoutProcessors, Math.Max(0, pipeline.LayoutProcessors.Count - 1),
            LayoutOperation.Field, ref reader, out var value, byteLength, continuation);
        if (result.Status != ReadResult.Success)
        {
            return result.Status == ReadResult.NeedMoreData
                ? LayoutReadResult<EncodedField>.NeedMoreData(result.RequestedByteCount)
                : LayoutReadResult<EncodedField>.WithStatus(result.Status);
        }
        return LayoutReadResult<EncodedField>.Success(value
            ?? throw EngineLogger.Fail($"'{_layoutContext.Field?.Path}': the layout succeeded without an encoded field."));
    }

    private void SetGroupData(GroupInfo group, int? itemIndex, int? itemCount)
    {
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        _layoutContext.FieldData.ByteLength = null;
        _layoutContext.FieldData.OpenWidthWindowBytes = DefaultLayoutProcessor.OpenWidthWindowBytes;
        _layoutContext.GroupData.ByteSize = group is not RepeatInfo && group.HasByteSize ? Resolve(group.ByteSize, group.Path, _instance) : null;
    }

    private ReadResult BeginLayout(GroupInfo group, ref SequenceReader<byte> reader, int? itemIndex = null, int? itemCount = null)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return ReadResult.Success;
        }
        _groupStarts.Push(reader.Consumed);
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        SetGroupData(group, itemIndex, itemCount);
        var chain = group.Pipeline.LayoutProcessors;
        foreach (var binding in group.Pipeline.OwnLayoutProcessors)
        {
            var index = IndexOf(chain, binding);
            var activeBinding = chain[index];
            BeginLayoutScope(activeBinding);
            _layoutContext.ReservedTail = 0;
            var status = RunLayout(chain, index, LayoutOperation.Begin, ref reader, out _).Status;
            if (status != ReadResult.Success)
            {
                return status;
            }
            if (!_layoutTails.TryGetValue(activeBinding, out var tails))
            {
                tails = new Stack<int>();
                _layoutTails.Add(activeBinding, tails);
            }
            tails.Push(_layoutContext.ReservedTail);
            _tailTotal += _layoutContext.ReservedTail;
        }
        return ReadResult.Success;
    }

    private ReadResult EndLayout(GroupInfo group, ref SequenceReader<byte> reader, int? itemIndex = null, int? itemCount = null)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return ReadResult.Success;
        }
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        SetGroupData(group, itemIndex, itemCount);
        var chain = group.Pipeline.LayoutProcessors;
        for (var i = group.Pipeline.OwnLayoutProcessors.Count - 1; i >= 0; i--)
        {
            var index = IndexOf(chain, group.Pipeline.OwnLayoutProcessors[i]);
            var binding = chain[index];
            _tailTotal -= _layoutTails[binding].Pop();
            var status = RunLayout(chain, index, LayoutOperation.End, ref reader, out _).Status;
            if (status != ReadResult.Success)
            {
                return status;
            }
            EndLayoutScope(binding);
        }
        _groupStarts.Pop();
        return ReadResult.Success;
    }

    private FieldReadResult<LogicalField> ReadSingle(ProcessorBinding binding, EncodedField encoded)
    {
        Prepare(_fieldContext, binding);
        return ((IFieldProcessor)binding.Processor).Read(encoded, _fieldContext);
    }

    /// <summary>Reverse order of writing: the last stage first (encoded to encoded), the head last (encoded to logical). The head decides the consumed width.</summary>
    private FieldReadResult<LogicalField> ReadChained(IReadOnlyList<ProcessorBinding> chain, EncodedField encoded, FieldInfo field)
    {
        _fieldContext.Field = field;
        for (var i = chain.Count - 1; i >= 1; i--)
        {
            Prepare(_fieldContext, chain[i]);
            var step = ((IFieldReader<EncodedField, EncodedField>)chain[i].Processor).Read(encoded, _fieldContext);
            if (step.Status != ReadResult.Success)
            {
                return new(step.Status, default!, 0);
            }
            encoded = step.Value;
        }
        return ReadSingle(chain[0], encoded);
    }

    private void SetPosition(long consumed)
    {
        _layoutContext.GroupData.RootPosition = consumed;
        _layoutContext.GroupData.Position = consumed - (_groupStarts.Count > 0 ? _groupStarts.Peek() : 0);
    }

    private void SetPosition(long rootPosition, ProcessorBinding binding)
    {
        _layoutContext.GroupData.RootPosition = rootPosition;
        _layoutContext.GroupData.Position = LayoutPosition(binding) - GroupStart(binding);
    }

    private long LayoutPosition(ProcessorBinding binding)
        => _layoutPositions.TryGetValue(binding, out var position) ? position : 0;

    private long GroupStart(ProcessorBinding binding)
        => _layoutGroupStarts.TryGetValue(binding, out var starts) && starts.Count > 0 ? starts.Peek().Position : 0;

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

    private static int IndexOf(IReadOnlyList<ProcessorBinding> chain, ProcessorBinding binding)
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

    private LayoutInput InputFor(ProcessorBinding binding)
    {
        if (!_layoutInputs.TryGetValue(binding, out var input))
        {
            input = new LayoutInput();
            _layoutInputs.Add(binding, input);
        }
        return input;
    }

    private LayoutReadResult RunLayout(IReadOnlyList<ProcessorBinding> chain, int index, LayoutOperation operation,
        ref SequenceReader<byte> reader, out EncodedField? value, int? byteLength = null, bool continuation = false)
    {
        value = null;
        var blockSize = chain.Count > 0 ? _layoutContext.BlockSizes.GetValueOrDefault(chain[index]) : null;
        var frames = new Stack<LayoutFrame>();
        frames.Push(new LayoutFrame(index, operation, byteLength, blockSize, continuation));
        try
        {
            while (true)
            {
                var frame = frames.Peek();
                var binding = chain.Count > 0 ? chain[frame.Index] : null;
                var input = frame.Index > 0 ? InputFor(binding!) : null;
                var end = Math.Max(reader.Consumed, ReadableEnd(ref reader));
                var available = input?.Remaining ?? end - reader.Consumed;
                var offered = input is null ? Math.Min(available, frame.Window) : available;
                var trial = input is null
                    ? new SequenceReader<byte>(reader.Sequence.Slice(0, reader.Consumed + offered))
                    : input.Reader();
                if (input is null)
                {
                    trial.Advance(reader.Consumed);
                }
                var start = trial.Consumed;
                var rootStart = reader.Consumed;
                Prepare(_layoutContext, binding, frame.Operation switch
                {
                    LayoutOperation.Begin => "BeginRead",
                    LayoutOperation.End => "EndRead",
                    _ => "read"
                });
                if (binding is null)
                {
                    SetPosition(rootStart);
                }
                else
                {
                    SetPosition(input is null ? rootStart : input.RootPosition + input.Consumed, binding);
                }
                _layoutContext.InputByteLength = frame.ByteLength;
                _layoutContext.IsReadContinuation = frame.Continuation;
                _layoutContext.BlockByteLength = frame.Operation == LayoutOperation.Bytes ? frame.BlockSize : null;
                _layoutContext.CanReserveTail = frame.Operation == LayoutOperation.Begin;
                var reservedTail = _layoutContext.ReservedTail;
                var processor = (ILayoutProcessor?)binding?.Processor ?? DefaultProcessors.LayoutProcessor;
                ReadOnlyMemory<byte> output = default;
                LayoutReadResult result;
                switch (frame.Operation)
                {
                    case LayoutOperation.Bytes:
                        var bytes = ((ILayoutReader<ReadOnlyMemory<byte>>)processor).Read(ref trial, _layoutContext);
                        output = bytes.Value;
                        result = new(bytes.Status, bytes.RequestedByteCount);
                        break;
                    case LayoutOperation.Begin:
                        result = processor.BeginRead(ref trial, _layoutContext);
                        break;
                    case LayoutOperation.End:
                        result = processor.EndRead(ref trial, _layoutContext);
                        break;
                    default:
                        var field = processor.Read(ref trial, _layoutContext);
                        value = field.Value;
                        result = new(field.Status, field.RequestedByteCount);
                        break;
                }
                _layoutContext.CanReserveTail = false;
                if (result.Status == ReadResult.NeedMoreData)
                {
                    _layoutContext.ReservedTail = reservedTail;
                    var initial = frame.ByteLength ?? DefaultLayoutProcessor.OpenWidthWindowBytes;
                    var target = result.RequestedByteCount ?? Math.Max(initial, offered * 2);
                    target = Math.Max(target, offered + 1);
                    if (target > Int32.MaxValue)
                    {
                        throw EngineLogger.Fail("The layout input window exceeds the supported byte count.");
                    }
                    if (input is null)
                    {
                        var next = (int)Math.Min(available, target);
                        if (next <= offered)
                        {
                            return LayoutReadResult.NeedMoreData(result.RequestedByteCount);
                        }
                        frame.Window = next;
                    }
                    else
                    {
                        var length = (int)(target - available);
                        if (frame.BlockSize is { } size)
                        {
                            length = Math.Min(size, length);
                        }
                        var requestedLength = result.RequestedByteCount is not null || frame.BlockSize is not null
                            ? (int?)length : null;
                        frames.Push(new LayoutFrame(frame.Index - 1, LayoutOperation.Bytes, requestedLength, frame.BlockSize,
                            frame.BlockSize is null && (frame.Continuation || frame.ParentCalls > 0)));
                    }
                    continue;
                }
                if (result.Status != ReadResult.Success)
                {
                    return result;
                }
                var consumed = trial.Consumed - start;
                if (input is null)
                {
                    reader.Advance(consumed);
                }
                else
                {
                    input.Consumed += checked((int)consumed);
                }
                if (binding is not null)
                {
                    _layoutPositions[binding] = LayoutPosition(binding) + consumed;
                }
                frames.Pop();
                if (frames.Count == 0)
                {
                    return result;
                }
                if (output.IsEmpty && consumed == 0)
                {
                    throw EngineLogger.Fail("A layout wrapper succeeded without consuming input or producing output.");
                }
                var child = frames.Peek();
                var childInput = InputFor(chain[child.Index]);
                if (childInput.Remaining == 0)
                {
                    childInput.RootPosition = rootStart + Math.Max(0, reader.Consumed - rootStart - output.Length) - childInput.Consumed;
                }
                childInput.Buffer.Write(output.Span);
                child.ParentCalls++;
            }
        }
        finally
        {
            _layoutContext.InputByteLength = null;
            _layoutContext.IsReadContinuation = false;
            _layoutContext.BlockByteLength = null;
            _layoutContext.CanReserveTail = false;
        }
    }

    /// <summary>Runs the stream stage (reverse order of writing) over the transport input into the payload.</summary>
    private ReadResult ReadStream(GroupInfo root, ReadOnlySequence<byte> input, out ReadOnlySequence<byte> payload)
    {
        _streamContext.Group = root;
        var processors = root.Pipeline.StreamProcessors;
        var current = input;
        payload = input;

        for (var i = processors.Count - 1; i >= 0; i--)
        {
            var binding = processors[i];
            var next = new ArrayBufferWriter<byte>();
            var reader = new SequenceReader<byte>(current);
            Prepare(_streamContext, binding);
            var result = ((IStreamProcessor)binding.Processor).Read(ref reader, next, _streamContext);
            if (i == processors.Count - 1)
            {
                _transportConsumed = reader.Consumed;
            }
            if (result != ReadResult.Success)
            {
                if (result == ReadResult.NeedMoreData)
                {
                    throw EngineLogger.Fail(
                        $"'{root.Path}': the stream processor '{binding.Processor.Name}' ({binding.Processor.Key}) needs more data in the input.");
                }
                return result;
            }
            current = new ReadOnlySequence<byte>(next.WrittenMemory);
        }

        payload = current;
        return ReadResult.Success;
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

    private void Prepare(ProcessorContext context, ProcessorBinding? binding, string direction = "read")
    {
        context.Current = binding!;
        context.ProcessorProperties = binding?.Properties ?? [];
        if (binding is not null)
        {
            EngineLogger.ProcessorExecuting(context.Stage, binding.LogCategory, direction, context.NodePath, _instance.ToLogString());
        }
    }
}
