using System.Buffers;

namespace Jacobi.BinarySerializer.Processor;

public interface IProcessor
{
    /// <summary>
    /// Textual short name identifier for the processor, used in schema definitions.
    /// </summary>
    string Id { get; }
    /// <summary>
    /// Full descriptive name for the processor, used in logging and diagnostics.
    /// </summary>
    string Name { get; }
    /// <summary>
    /// The pipeline stage in which this processor operates.
    /// </summary>
    PipelineStage Stage { get; }
}

// Semantic pipeline stage: logical value transforms (scale, enum mapping, nullability, etc.)
public interface IValueProcessor : IValueWriter<LogicalField, LogicalField>, IValueReader<LogicalField, LogicalField> { }
public interface IValueWriter<InT, OutT> : IProcessor
{
    OutT Write(InT logicalValue, ValueProcessorContext context);
}
public interface IValueReader<InT, OutT> : IProcessor
{
    OutT Read(InT logicalValue, ValueProcessorContext context);
}

// Representation pipeline stage: physical representation transforms (varint, fixed-width, etc.)
public interface IFieldProcessor : IFieldWriter<LogicalField, EncodedField>, IFieldReader<EncodedField, LogicalField> { }
public interface IFieldWriter<InT, OutT> : IProcessor
{
    OutT Write(InT field, FieldProcessorContext context);
}
public interface IFieldReader<InT, OutT> : IProcessor
{
    OutT Read(InT field, FieldProcessorContext context);
}

// Layout pipeline stage: bit packing, alignment, endian conversion, etc.
public interface ILayoutProcessor : ILayoutWriter<EncodedField>, ILayoutReader<EncodedField> { }
public interface ILayoutWriter<InT> : IProcessor
{
    void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context);
    WriteResult Write(IBufferWriter<byte> writer, InT encodedValue, LayoutProcessorContext context);
    void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context);
}
public interface ILayoutReader<OutT> : IProcessor
{
    void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context);
    ReadResult Read(ref SequenceReader<byte> reader, out OutT outValue, LayoutProcessorContext context);
    void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context);
}

// Stream pipeline stage: framing, compression, encryption, etc.
public interface IStreamProcessor : IProcessor
{
    WriteResult Write(ref SequenceReader<byte> payloadInput, IBufferWriter<byte> transportOutput, StreamProcessorContext context);
    ReadResult Read(ref SequenceReader<byte> transportInput, IBufferWriter<byte> payloadOutput, StreamProcessorContext context);
}

public enum WriteResult
{
    /// <summary>Write operation failed (abort).</summary>
    Failure,
    /// <summary>Write operation was successful.</summary>
    Success,
    /// <summary>More room is needed to complete the write operation.</summary>
    NeedMoreSpace,
    /// <summary>More data is needed to complete the write operation.</summary>
    NeedMoreData,
}

public enum ReadResult
{
    /// <summary>Read operation failed (abort).</summary>
    Failure,
    /// <summary>Read operation was successful.</summary>
    Success,
    /// <summary>Clean end. No more data available.</summary>
    EndOfData,
    /// <summary>More data is needed to complete the read operation.</summary>
    NeedMoreData,
}

public sealed class NullValueProcessor : IValueProcessor
{
    public string Id => "null-value";
    public string Name => "Null Value Processor";
    public PipelineStage Stage => PipelineStage.Semantic;

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
        => logicalValue;

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
        => logicalValue;
}

public sealed class NullFieldProcessor : IFieldProcessor
{
    public string Id => "null-field";
    public string Name => "Null Field Processor";
    public PipelineStage Stage => PipelineStage.Representation;

    public EncodedField Write(LogicalField field, FieldProcessorContext context)
        // TODO: Determine bit width based on type and value.
        => new(field.Name, field.LogicalType, field.Value, 1);

    public LogicalField Read(EncodedField field, FieldProcessorContext context)
        => new(field.Name, field.PhysicalType, field.Value);
}

// this null-processor should probably be short-circuited in the pipeline/session.
public sealed class NullLayoutProcessor : ILayoutProcessor
{
    public string Id => "null-layout";
    public string Name => "Null Layout Processor";
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
public sealed class NullStreamProcessor : IStreamProcessor
{
    public string Id => "null-stream";
    public string Name => "Null Stream Processor";
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

public sealed record LogicalField(string Name, Type LogicalType, object? Value);
public sealed record EncodedField(string Name, Type PhysicalType, object? Value, int BitWidth);
