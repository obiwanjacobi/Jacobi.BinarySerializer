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
public sealed class ReaderSession : SessionState
{
    private readonly ExecutionPlan _plan;
    private readonly IServiceProvider _services;

    private readonly ValueProcessorContext _valueContext;
    private readonly FieldProcessorContext _fieldContext;
    private readonly LayoutProcessorContext _layoutContext;
    private readonly StreamProcessorContext _streamContext;

    public ReaderSession(ExecutionPlan plan, IServiceProvider? services = null)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _services = services ?? WriterSession.EmptyServiceProvider.Instance;

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
        var cursor = new PlanCursor<IValueSink>(root, range);
        while (true)
        {
            var step = cursor.Next();
            switch (step.Kind)
            {
                case CursorStepKind.EnterGroup:
                {
                    var group = (GroupInfo)step.Node!;
                    var scope = step.Scope is null
                        ? sink
                        : step.Scope.EnterGroup(new GroupContext { Node = group, Services = _services });
                    cursor.Enter(scope);
                    BeginLayout(group, ref reader);
                    break;
                }

                case CursorStepKind.Field:
                {
                    var result = ReadField((FieldInfo)step.Node!, step.Scope!, ref reader);
                    if (result != ReadResult.Success)
                    {
                        return result;
                    }
                    break;
                }

                case CursorStepKind.ExitGroup:
                    EndLayout((GroupInfo)step.Node!, ref reader);
                    step.Scope!.Complete();
                    break;

                case CursorStepKind.Done:
                    return ReadResult.Success;
            }
        }
    }

    private ReadResult ReadField(FieldInfo field, IValueSink scope, ref SequenceReader<byte> reader)
    {
        var pipeline = field.Pipeline;

        // Layout: bytes -> encoded
        _layoutContext.Group = field.Parent!;
        _layoutContext.Field = field;
        EncodedField encoded;
        ReadResult layoutResult;
        switch (pipeline.LayoutProcessors.Count)
        {
            case 0:
                Prepare(_layoutContext, null);
                layoutResult = ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, out encoded, _layoutContext);
                break;
            case 1:
                var layoutBinding = pipeline.LayoutProcessors[0];
                Prepare(_layoutContext, layoutBinding);
                layoutResult = ((ILayoutProcessor)layoutBinding.Processor).Read(ref reader, out encoded, _layoutContext);
                break;
            default:
                // TODO: several layout processors reading from the same input.
                throw new NotSupportedException($"'{field.Path}': multiple Layout processors are not supported.");
        }

        if (layoutResult != ReadResult.Success)
        {
            return layoutResult;
        }

        // Representation: encoded -> logical
        LogicalField logical;
        switch (pipeline.FieldProcessors.Count)
        {
            case 0:
                if (encoded.Value is not byte[] bytes || !DataTypeCodec.TryDecode(field.Field.Type, bytes, out var value))
                {
                    throw new InvalidOperationException($"'{field.Path}': cannot decode the bytes as {field.Field.Type}.");
                }
                logical = new LogicalField(encoded.Name, DataTypeCodec.ClrType(field.Field.Type) ?? typeof(object), value);
                break;
            case 1:
                var fieldBinding = pipeline.FieldProcessors[0];
                _fieldContext.Field = field;
                Prepare(_fieldContext, fieldBinding);
                logical = ((IFieldProcessor)fieldBinding.Processor).Read(encoded, _fieldContext);
                break;
            default:
                // TODO: field processors cannot be chained (EncodedField -> LogicalField).
                throw new NotSupportedException($"'{field.Path}': multiple Representation processors are not supported.");
        }

        // Semantic: reverse order of writing
        _valueContext.Field = field;
        var valueProcessors = pipeline.ValueProcessors;
        for (var i = valueProcessors.Count - 1; i >= 0; i--)
        {
            Prepare(_valueContext, valueProcessors[i]);
            logical = ((IValueProcessor)valueProcessors[i].Processor).Read(logical, _valueContext);
        }

        scope.SetField(new FieldContext { Node = field, Services = _services }, logical);
        return ReadResult.Success;
    }

    private void BeginLayout(GroupInfo group, ref SequenceReader<byte> reader)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return;
        }

        _layoutContext.Group = group;
        _layoutContext.Field = null;
        foreach (var binding in group.Pipeline.LayoutProcessors)
        {
            Prepare(_layoutContext, binding);
            ((ILayoutProcessor)binding.Processor).BeginRead(ref reader, _layoutContext);
        }
    }

    private void EndLayout(GroupInfo group, ref SequenceReader<byte> reader)
    {
        if (!WriterSession.OwnsLayout(group))
        {
            return;
        }

        _layoutContext.Group = group;
        _layoutContext.Field = null;
        foreach (var binding in group.Pipeline.LayoutProcessors)
        {
            Prepare(_layoutContext, binding);
            ((ILayoutProcessor)binding.Processor).EndRead(ref reader, _layoutContext);
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
            if (result != ReadResult.Success)
            {
                return result;
            }
            current = new ReadOnlySequence<byte>(next.WrittenMemory);
        }

        payload = current;
        return ReadResult.Success;
    }

    private static void Prepare(ProcessorContext context, ProcessorBinding? binding)
    {
        context.Current = binding!;
        context.ProcessorProperties = binding?.Properties ?? [];
    }
}
