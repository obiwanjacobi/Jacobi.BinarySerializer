using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

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
public sealed class ReaderSession : SessionState
{
    private readonly ExecutionPlan _plan;
    private readonly IServiceProvider _services;
    private readonly bool _valueFieldsUseModel;
    private InstancePath _instance;
    private readonly Stack<long> _groupStarts = new();
    private readonly Stack<int> _choiceIndexes = new();
    private readonly Stack<(GroupInfo Group, long End)> _windows = new();

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
        _windows.Clear();
        EngineLogger.ReadStarted(root.Path.ToString());
        var cursor = new PlanCursor<IValueSink>(root, range);
        while (true)
        {
            if (cursor.AtOpenRepeat && AtWindowEnd(ref reader))
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
                            if (group is RepeatInfo { UntilEnd: true })
                            {
                                cursor.EnterOpenRepeat(sink);
                            }
                            else if (group is RepeatInfo rootRepeat)
                            {
                                cursor.EnterRepeat(sink, Resolve(rootRepeat.Count, rootRepeat, _valueContext, _instance));
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
                        else if (group is ChoiceInfo choice)
                        {
                            var index = Resolve(choice.SelectedIndex, choice, _valueContext, _instance);
                            EngineLogger.ChoiceSelected(choice.Path.ToString(), index);
                            _choiceIndexes.Push(index);
                            var choiceScope = step.Scope.EnterChoice(new ChoiceContext { Node = choice, Services = _services, Instance = _instance }, index);                            cursor.Enter(choiceScope, index);
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
                            _windows.Push((group, reader.Consumed + size));
                        }
                        if (group is not RepeatInfo)
                        {
                            BeginLayout(group, ref reader);
                        }
                        break;
                    }

                case CursorStepKind.Field:
                    {
                        var fieldNode = (FieldInfo)step.Node!;
                        EngineLogger.ReadingField(fieldNode.Path.ToString(), _instance.ToString());
                        var result = ReadField(fieldNode, step.Scope!, ref reader);
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
                        BeginLayout(repeat, ref reader, step.Index, step.Count);
                        break;
                    }

                case CursorStepKind.ExitItem:
                    EndLayout((GroupInfo)step.Node!, ref reader, step.Index, step.Count);
                    step.Scope!.Complete();
                    break;

                case CursorStepKind.ExitGroup:
                    if (step.Node is not RepeatInfo)
                    {
                        EndLayout((GroupInfo)step.Node!, ref reader);
                        step.Scope!.Complete();
                    }
                    if (step.Node is ChoiceInfo)
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
                    EngineLogger.ReadFinished(root.Path.ToString());
                    return ReadResult.Success;
            }
        }
    }

    private bool AtWindowEnd(ref SequenceReader<byte> reader)
        => _windows.Count > 0 ? reader.Consumed >= _windows.Peek().End : reader.End;

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
        var fieldLength = BytesLength(field, ref reader);
        _layoutContext.FieldData.ByteLength = fieldLength;
        _fieldContext.FieldData.ByteLength = fieldLength;
        if (fieldLength is { } bytesLength && reader.Remaining < bytesLength)
        {
            return ReadResult.NeedMoreData;
        }
        EncodedField encoded;
        LayoutReadResult<EncodedField> layoutRead;
        switch (pipeline.LayoutProcessors.Count)
        {
            case 0:
                Prepare(_layoutContext, null);
                SetPosition(reader.Consumed);
                layoutRead = ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, _layoutContext);
                break;
            case 1:
                var layoutBinding = pipeline.LayoutProcessors[0];
                Prepare(_layoutContext, layoutBinding);
                SetPosition(reader.Consumed);
                layoutRead = ((ILayoutProcessor)layoutBinding.Processor).Read(ref reader, _layoutContext);
                break;
            default:
                layoutRead = ReadChained(pipeline.LayoutProcessors, ref reader);
                break;
        }

        if (layoutRead.Status != ReadResult.Success)
        {
            return layoutRead.Status;
        }
        encoded = layoutRead.Value;

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
                    needsLargerWindow = fieldResult.Status == ReadResult.NeedMoreData && reader.Remaining > provided / 8;
                    return fieldResult.Status;
                }
                if (fieldResult.BitsConsumed < 0 || fieldResult.BitsConsumed > provided || fieldResult.BitsConsumed % 8 != 0)
                {
                    throw EngineLogger.Fail(
                        $"'{field.Path}': the field processor consumed {fieldResult.BitsConsumed} bits of the {provided} bits provided.");
                }

                // give back the part of the window the processor did not use.
                reader.Rewind((provided - fieldResult.BitsConsumed) / 8);
                logical = fieldResult.Value;
                break;
        }

        // Semantic: reverse order of writing
        _valueContext.Field = field;
        var valueProcessors = pipeline.ValueProcessors;
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
        return ReadResult.Success;
    }

    private void SetGroupData(GroupInfo group, int? itemIndex, int? itemCount)
    {
        _layoutContext.GroupData.SetNode(group, itemIndex, itemCount, group is ChoiceInfo ? _choiceIndexes.Peek() : null);
        _layoutContext.FieldData.ByteLength = null;
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
        SetGroupData(group, itemIndex, itemCount);
        var chain = group.Pipeline.LayoutProcessors;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            Prepare(_layoutContext, chain[i]);
            SetPosition(reader.Consumed);
            ((ILayoutProcessor)chain[i].Processor).BeginRead(ref reader, _layoutContext);
        }
    }

    private void EndLayout(GroupInfo group, ref SequenceReader<byte> reader, int? itemIndex = null, int? itemCount = null)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return;
        }

        _layoutContext.Group = group;
        _layoutContext.Field = null;
        SetGroupData(group, itemIndex, itemCount);
        foreach (var binding in group.Pipeline.LayoutProcessors)
        {
            Prepare(_layoutContext, binding);
            SetPosition(reader.Consumed);
            ((ILayoutProcessor)binding.Processor).EndRead(ref reader, _layoutContext);
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

    /// <summary>
    /// Reads a field through a chain: the last processor sees the actual input first and passes the unread rest on (towards the head).
    /// The chained stages only strip what they own (e.g. padding); the head reads the field. The actual reader advances by what was consumed in total.
    /// </summary>
    private LayoutReadResult<EncodedField> ReadChained(IReadOnlyList<ProcessorBinding> chain, ref SequenceReader<byte> reader)
    {
        var start = reader.Consumed;
        var initialRemaining = reader.Remaining;
        var current = reader.UnreadSequence;

        for (var j = chain.Count - 1; j >= 1; j--)
        {
            var stageReader = new SequenceReader<byte>(current);
            Prepare(_layoutContext, chain[j]);
            SetPosition(start + (initialRemaining - current.Length));
            var result = ((ILayoutReader<ReadOnlyMemory<byte>>)chain[j].Processor).Read(ref stageReader, _layoutContext);
            if (result.Status != ReadResult.Success)
            {
                return LayoutReadResult<EncodedField>.WithStatus(result.Status);
            }
            current = new ReadOnlySequence<byte>(result.Value);
        }

        var headReader = new SequenceReader<byte>(current);
        Prepare(_layoutContext, chain[0]);
        SetPosition(start + (initialRemaining - current.Length));
        var headResult = ((ILayoutProcessor)chain[0].Processor).Read(ref headReader, _layoutContext);
        if (headResult.Status == ReadResult.Success)
        {
            reader.Advance(initialRemaining - headReader.Remaining);
        }
        return headResult;
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
        context.Current = binding!;
        context.ProcessorProperties = binding?.Properties ?? [];
    }
}
