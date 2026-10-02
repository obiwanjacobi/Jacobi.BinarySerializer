using System.Buffers;

namespace Jacobi.BinarySerializer.Processor;

public static class ProcessorDefaults
{
    public static IProcessor For(PipelineStage stage)
        => stage switch
        {
            PipelineStage.Semantic => DefaultValueProcessor,
            PipelineStage.Representation => DefaultFieldProcessor,
            PipelineStage.Layout => DefaultLayoutProcessor,
            PipelineStage.Stream => DefaultStreamProcessor,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown pipeline stage.")
        };

    public static readonly DefaultValueProcessor DefaultValueProcessor = new();
    public static readonly DefaultFieldProcessor DefaultFieldProcessor = new();
    public static readonly DefaultLayoutProcessor DefaultLayoutProcessor = new();
    public static readonly DefaultStreamProcessor DefaultStreamProcessor = new();
}

public sealed class DefaultValueProcessor : IValueProcessor
{
    public ProcessorKey Key => new("default", "value");
    public string Name => "Default Value Processor";
    public PipelineStage Stage => PipelineStage.Semantic;

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
        => logicalValue;

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
        => logicalValue;
}

public sealed class DefaultFieldProcessor : IFieldProcessor
{
    public ProcessorKey Key => new("default", "field");
    public string Name => "Default Field Processor";
    public PipelineStage Stage => PipelineStage.Representation;

    public EncodedField Write(LogicalField field, FieldProcessorContext context)
        // TODO: Determine bit width based on type and value.
        => new(field.Name, field.LogicalType, field.Value, 1);

    public LogicalField Read(EncodedField field, FieldProcessorContext context)
        => new(field.Name, field.PhysicalType, field.Value);
}

// this null-processor should probably be short-circuited in the pipeline/session.
public sealed class DefaultLayoutProcessor : ILayoutProcessor
{
    public ProcessorKey Key => new("default", "layout");
    public string Name => "Default Layout Processor";
    public PipelineStage Stage => PipelineStage.Layout;

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        return WriteResult.Success;
    }
    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }

    public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
    public ReadResult Read(ref SequenceReader<byte> reader, out EncodedField encodedValue, LayoutProcessorContext context)
    {
        // TODO:
        encodedValue = new EncodedField(string.Empty, typeof(object), null, 0);
        return ReadResult.Success;
    }
    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
}

// this null-processor should probably be short-circuited in the pipeline/session.
public sealed class DefaultStreamProcessor : IStreamProcessor
{
    public ProcessorKey Key => new("default", "stream");
    public string Name => "Default Stream Processor";
    public PipelineStage Stage => PipelineStage.Stream;

    public WriteResult Write(ref SequenceReader<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
    {
        foreach (var segment in input.Sequence)
        {
            output.Write(segment.Span);
        }
        return WriteResult.Success;
    }
    public ReadResult Read(ref SequenceReader<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
    {
        foreach (var segment in input.Sequence)
        {
            output.Write(segment.Span);
        }
        return ReadResult.Success;
    }
}
