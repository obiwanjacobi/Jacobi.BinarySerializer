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
    private readonly Dictionary<ProcessorBinding, Stack<long>> _layoutGroupStarts = [];
    private readonly Stack<int> _choiceIndexes = new();
    private readonly Stack<(GroupInfo Group, long End, int AncestorTail)> _windows = new();
    private readonly Stack<int> _tails = new();
    private readonly Dictionary<GroupInfo, int> _minItemBytes = new();
    private int _tailTotal;
    private long _transportConsumed;

    /// <summary>
    /// The physical bytes consumed from the input by the last read (the transport bytes when the root has stream processors).
    /// </summary>
    public long BytesRead { get; private set; }

    private readonly ValueProcessorContext _valueContext;
    private readonly FieldProcessorContext _fieldContext;
    private readonly PackingProcessorContext _packingContext;
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
        _packingContext = new PackingProcessorContext(this) { Services = _services, Stage = PipelineStage.Packing };
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
        _windows.Clear();
        _tails.Clear();
        _tailTotal = 0;
        EngineLogger.ReadStarted(root.Path.ToString());
        var cursor = new PlanCursor<IValueSink>(root, range, EngineLogger);
        while (true)
        {
            if (cursor.AtOpenRepeat && AtOpenRepeatEnd(ref reader, (RepeatInfo)cursor.CurrentGroup!))
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
                            var body = _plan.ResolveOpenChoiceBody(openChoice, selected.Group, selected.Target);
                            _openBody = (body, selected.Scope);
                            cursor.EnterOpenChoice(step.Scope, body, _plan.CreateOpenChoiceRange(openChoice, body, selected.Target));
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
                            BeginLayout(group, ref reader);
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

                case CursorStepKind.PackedGroup:
                    {
                        var group = (PackedGroupInfo)step.Node!;
                        var result = ReadPackedGroup(group, step.Scope!, ref reader);
                        if (result != ReadResult.Success)
                        {
                            EngineLogger.ReadStopped(group.Path.ToString(), result);
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
                        BeginLayout(repeat, ref reader, step.Index, step.Count);
                        break;
                    }

                case CursorStepKind.ExitItem:
                    EndLayout((GroupInfo)step.Node!, ref reader, step.Index, step.Count);
                    step.Scope!.ExitItem(
                        new RepeatContext { Node = (RepeatInfo)step.Node!, Services = _services, Instance = _instance }, step.Index, step.Count);
                    break;

                case CursorStepKind.ExitGroup:
                    if (step.Node is not RepeatInfo and not ChoiceInfo { IsOpen: true })
                    {
                        EndLayout((GroupInfo)step.Node!, ref reader);
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

    private bool AtOpenRepeatEnd(ref SequenceReader<byte> reader, RepeatInfo repeat)
    {
        if (ContainsPackedGroup(repeat))
        {
            return reader.Consumed >= PackedReadEnd(ref reader);
        }
        if (_windows.Count > 0)
        {
            return reader.Consumed >= _windows.Peek().End;
        }
        if (!_minItemBytes.TryGetValue(repeat, out var min))
        {
            min = repeat.Members.Sum(MinBytes);
            _minItemBytes[repeat] = min;
        }
        return reader.Remaining - _tailTotal < Math.Max(min, 1);
    }

    private static bool ContainsPackedGroup(GroupInfo group)
        => group is PackedGroupInfo || group.Members.OfType<GroupInfo>().Any(ContainsPackedGroup);

    private long PackedReadEnd(ref SequenceReader<byte> reader)
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
            PackedGroupInfo { ByteLength: int length } => length,
            PackedGroupInfo => 0,
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

        if (byteOffset >= 0)
        {
            reader.Advance(byteOffset);
        }
        else
        {
            reader.Rewind(-byteOffset);
        }

        var fieldStart = reader.Consumed;
        var result = ReadField(field, scope, ref reader);
        var bytesConsumed = reader.Consumed - fieldStart;
        if (bytesConsumed > 0)
        {
            RewindLayoutPositions(field.Pipeline.LayoutProcessors, bytesConsumed);
        }

        var delta = reader.Consumed - start;
        if (delta > 0)
        {
            reader.Rewind(delta);
        }
        else if (delta < 0)
        {
            reader.Advance(-delta);
        }
        return result;
    }

    private ReadResult ReadField(FieldInfo field, IValueSink scope, ref SequenceReader<byte> reader)
    {
        // A field processor decides its own width. When it needs more data than the window offered and the input has more,
        // retry with a bigger window; only when the window already covered all unread input is it NeedMoreData for the caller.
        var window = DefaultLayoutProcessor.OpenWidthWindowBytes;
        while (true)
        {
            _layoutContext.FieldData.OpenWidthWindowBytes = window;
            var result = ReadFieldWindow(field, scope, ref reader, out var needsLargerWindow);
            if (!needsLargerWindow)
            {
                return result;
            }
            window = window > Int32.MaxValue / 2 ? Int32.MaxValue : window * 2;
        }
    }

    private ReadResult ReadFieldWindow(FieldInfo field, IValueSink scope, ref SequenceReader<byte> reader, out bool needsLargerWindow)
    {
        needsLargerWindow = false;
        var pipeline = field.Pipeline;

        // Layout: bytes -> encoded
        _layoutContext.Group = field.Parent!;
        _layoutContext.Field = field;
        _layoutContext.PackedGroup = null;
        var fieldLength = BytesLength(field, ref reader);
        _layoutContext.FieldData.ByteLength = fieldLength;
        _fieldContext.FieldData.ByteLength = fieldLength;
        if (fieldLength is { } bytesLength && reader.Remaining < bytesLength)
        {
            return ReadResult.NeedMoreData;
        }
        var layoutRead = ReadLayout(pipeline, ref reader);
        if (layoutRead.Status != ReadResult.Success)
        {
            return layoutRead.Status;
        }
        var encoded = layoutRead.Value;

        // Representation: encoded -> logical
        LogicalField logical;
        switch (pipeline.FieldProcessors.Count)
        {
            case 0:
                if (encoded.Value is not byte[] bytes || field.DataType.Decode is not { } decode || !decode(bytes, out var value))
                {
                    throw EngineLogger.Fail($"'{field.Path}': cannot decode the bytes as {field.DataType}.");
                }
                logical = new LogicalField(encoded.Name, field.DataType.ClrType, value);
                break;
            default:
                var provided = encoded.BitWidth;
                _fieldContext.Field = field;
                var fieldResult = pipeline.FieldProcessors.Count == 1
                    ? ReadSingle(pipeline.FieldProcessors[0], encoded)
                    : ReadChained(pipeline.FieldProcessors, encoded, field);

                if (fieldResult.Status != ReadResult.Success)
                {
                    reader.Rewind(provided / 8);
                    RewindLayoutPositions(pipeline.LayoutProcessors, provided / 8);
                    needsLargerWindow = fieldResult.Status == ReadResult.NeedMoreData && reader.Remaining > provided / 8;
                    return fieldResult.Status;
                }
                if (fieldResult.BitsConsumed < 0 || fieldResult.BitsConsumed > provided || fieldResult.BitsConsumed % 8 != 0)
                {
                    throw EngineLogger.Fail(
                        $"'{field.Path}': the field processor consumed {fieldResult.BitsConsumed} bits of the {provided} bits provided.");
                }

                // give back the part of the window the processor did not use.
                var unusedBytes = (provided - fieldResult.BitsConsumed) / 8;
                reader.Rewind(unusedBytes);
                RewindLayoutPositions(pipeline.LayoutProcessors, unusedBytes);
                logical = fieldResult.Value;
                break;
        }

        CompleteLogicalField(field, logical, scope);
        return ReadResult.Success;
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

    private ReadResult ReadPackedGroup(PackedGroupInfo group, IValueSink scope, ref SequenceReader<byte> reader)
    {
        var byteLength = Resolve(group.ByteLength, group.Path, _instance);
        ValidateCarrierLength(group, byteLength);
        var end = PackedReadEnd(ref reader);
        if (end - reader.Consumed < byteLength)
        {
            return ReadResult.NeedMoreData;
        }
        var bounded = new SequenceReader<byte>(reader.Sequence.Slice(0, end));
        bounded.Advance(reader.Consumed);
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        _layoutContext.PackedGroup = group;
        _layoutContext.FieldData.ByteLength = byteLength;
        var layout = ReadLayout(group.Pipeline, ref bounded);
        if (layout.Status != ReadResult.Success)
        {
            return layout.Status == ReadResult.EndOfData ? ReadResult.NeedMoreData : layout.Status;
        }
        var carrier = PrepareCarrier(group, layout.Value, layout.Value?.BitWidth ?? 0, byteLength);
        _packingContext.Group = group;
        _packingContext.ByteLength = byteLength;
        _packingContext.Instance = _instance;
        Prepare(_packingContext, group.PackingProcessor);
        var result = ((IPackingProcessor)group.PackingProcessor.Processor).Read(carrier, _packingContext);
        if (result.Status != ReadResult.Success)
        {
            return result.Status == ReadResult.EndOfData ? ReadResult.NeedMoreData : result.Status;
        }
        if (result.BitsConsumed != checked(byteLength * 8))
        {
            throw EngineLogger.Fail($"'{group.Path}': the packing processor consumed {result.BitsConsumed} bits but the carrier requires {byteLength * 8}.");
        }
        if (result.Value is null || result.Value.Count != group.Fields.Count)
        {
            throw EngineLogger.Fail($"'{group.Path}': the packing processor must return exactly {group.Fields.Count} logical fields.");
        }
        for (var i = 0; i < group.Fields.Count; i++)
        {
            if (result.Value[i] is not { } logical || logical.Name != group.Fields[i].Name)
            {
                throw EngineLogger.Fail($"'{group.Path}': packed field {i} must be '{group.Fields[i].Name}' in declaration order.");
            }
        }
        reader.Advance(bounded.Consumed - reader.Consumed);
        for (var i = 0; i < group.Fields.Count; i++)
        {
            CompleteLogicalField(group.Fields[i], result.Value[i], scope);
        }
        return ReadResult.Success;
    }

    private LayoutReadResult<EncodedField> ReadLayout(ProcessorPipeline pipeline, ref SequenceReader<byte> reader)
    {
        switch (pipeline.LayoutProcessors.Count)
        {
            case 0:
                Prepare(_layoutContext, null);
                SetPosition(reader.Consumed);
                return DefaultProcessors.LayoutProcessor.Read(ref reader, _layoutContext);
            case 1:
                var binding = pipeline.LayoutProcessors[0];
                Prepare(_layoutContext, binding);
                SetPosition(reader.Consumed, binding);
                var start = reader.Consumed;
                var singleResult = ((ILayoutProcessor)binding.Processor).Read(ref reader, _layoutContext);
                if (singleResult.Status == ReadResult.Success)
                {
                    AdvanceReadPosition(pipeline.LayoutProcessors, 0, reader.Consumed - start);
                }
                return singleResult;
            default:
                return ReadChained(pipeline.LayoutProcessors, ref reader);
        }
    }

    private void SetGroupData(GroupInfo group, int? itemIndex, int? itemCount)
    {
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        _layoutContext.FieldData.ByteLength = null;
        _layoutContext.PackedGroup = null;
        _layoutContext.GroupData.ByteSize = group is not RepeatInfo && group.HasByteSize ? Resolve(group.ByteSize, group.Path, _instance) : null;
    }

    private void BeginLayout(GroupInfo group, ref SequenceReader<byte> reader, int? itemIndex = null, int? itemCount = null)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return;
        }

        _groupStarts.Push(reader.Consumed);
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        _layoutContext.ReservedTail = 0;
        _layoutContext.CanReserveTail = true;
        SetGroupData(group, itemIndex, itemCount);
        var chain = group.Pipeline.LayoutProcessors;
        foreach (var binding in group.Pipeline.OwnLayoutProcessors)
        {
            var index = IndexOf(chain, binding);
            if (index < 0)
            {
                continue;
            }
            var activeBinding = chain[index];
            BeginLayoutScope(activeBinding);
            Prepare(_layoutContext, activeBinding, "BeginRead");
            SetPosition(reader.Consumed, activeBinding);
            var start = reader.Consumed;
            ((ILayoutProcessor)activeBinding.Processor).BeginRead(ref reader, _layoutContext);
            AdvanceReadPosition(chain, index, reader.Consumed - start);
        }
        _layoutContext.CanReserveTail = false;
        _tails.Push(_layoutContext.ReservedTail);
        _tailTotal += _layoutContext.ReservedTail;
    }

    private void EndLayout(GroupInfo group, ref SequenceReader<byte> reader, int? itemIndex = null, int? itemCount = null)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return;
        }

        _tailTotal -= _tails.Pop();
        _layoutContext.Group = group;
        _layoutContext.Field = null;
        SetGroupData(group, itemIndex, itemCount);
        var chain = group.Pipeline.LayoutProcessors;
        for (var i = group.Pipeline.OwnLayoutProcessors.Count - 1; i >= 0; i--)
        {
            var binding = group.Pipeline.OwnLayoutProcessors[i];
            var index = IndexOf(chain, binding);
            if (index < 0)
            {
                continue;
            }
            var activeBinding = chain[index];
            Prepare(_layoutContext, activeBinding, "EndRead");
            SetPosition(reader.Consumed, activeBinding);
            var start = reader.Consumed;
            ((ILayoutProcessor)activeBinding.Processor).EndRead(ref reader, _layoutContext);
            AdvanceReadPosition(chain, index, reader.Consumed - start);
            EndLayoutScope(activeBinding);
        }
        _groupStarts.Pop();
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
        => _layoutGroupStarts.TryGetValue(binding, out var starts) && starts.Count > 0 ? starts.Peek() : 0;

    private void AdvanceReadPosition(IReadOnlyList<ProcessorBinding> chain, int index, long count)
    {
        for (var i = 0; i <= index; i++)
        {
            _layoutPositions[chain[i]] = LayoutPosition(chain[i]) + count;
        }
    }

    private void AdvanceFieldPositions(IReadOnlyList<ProcessorBinding> chain, IReadOnlyList<long> consumed)
    {
        long downstream = 0;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            downstream += consumed[i];
            _layoutPositions[chain[i]] = LayoutPosition(chain[i]) + downstream;
        }
    }

    private void RewindLayoutPositions(IReadOnlyList<ProcessorBinding> chain, long count)
    {
        foreach (var binding in chain)
        {
            _layoutPositions[binding] = LayoutPosition(binding) - count;
        }
    }

    private void BeginLayoutScope(ProcessorBinding binding)
    {
        if (!_layoutGroupStarts.TryGetValue(binding, out var starts))
        {
            starts = new Stack<long>();
            _layoutGroupStarts.Add(binding, starts);
        }
        starts.Push(LayoutPosition(binding));
    }

    private void EndLayoutScope(ProcessorBinding binding)
        => _layoutGroupStarts[binding].Pop();

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

    /// <summary>
    /// Reads bytes through the layout wrappers
    /// and returns the remainder for the next stage; the innermost processor extracts the encoded field.
    /// </summary>
    private LayoutReadResult<EncodedField> ReadChained(IReadOnlyList<ProcessorBinding> chain, ref SequenceReader<byte> reader)
    {
        var start = reader.Consumed;
        var current = reader.UnreadSequence;
        var consumed = new long[chain.Count];
        var rootPosition = start;

        for (var i = 0; i < chain.Count - 1; i++)
        {
            var stageReader = new SequenceReader<byte>(current);
            Prepare(_layoutContext, chain[i]);
            SetPosition(rootPosition, chain[i]);
            var result = ((ILayoutReader<ReadOnlyMemory<byte>>)chain[i].Processor).Read(ref stageReader, _layoutContext);
            if (result.Status != ReadResult.Success)
            {
                return LayoutReadResult<EncodedField>.WithStatus(result.Status);
            }
            consumed[i] = stageReader.Consumed;
            rootPosition += stageReader.Consumed;
            current = new ReadOnlySequence<byte>(result.Value);
        }

        var inner = chain.Count - 1;
        var innerReader = new SequenceReader<byte>(current);
        Prepare(_layoutContext, chain[inner]);
        SetPosition(rootPosition, chain[inner]);
        var innerResult = ((ILayoutProcessor)chain[inner].Processor).Read(ref innerReader, _layoutContext);
        if (innerResult.Status == ReadResult.Success)
        {
            consumed[inner] = innerReader.Consumed;
            AdvanceFieldPositions(chain, consumed);
            reader.Advance(consumed.Sum());
        }
        return innerResult;
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
