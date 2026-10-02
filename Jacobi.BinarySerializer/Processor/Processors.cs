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

    //bool CanEncode(Type logicalType, ProcessorContext context);
    //bool CanDecode(Type logicalType, ProcessorContext context);
}

// Semantic pipeline stage: logical value transforms (scale, enum mapping, nullability, etc.)
public interface IValueProcessor : IProcessor
{
    object Encode(object logicalValue, ValueProcessorContext context);
    object Decode(object physicalValue, ValueProcessorContext context);
}

// Representation pipeline stage: physical representation transforms (varint, fixed-width, etc.)
public interface IFieldProcessor : IProcessor
{
    EncodedField EncodeField(LogicalField field, FieldProcessorContext context);
    LogicalField DecodeField(EncodedField field, FieldProcessorContext context);
}

// Layout pipeline stage: bit packing, alignment, endian conversion, etc.
public interface ILayoutProcessor : IProcessor
{
    void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context);
    void WriteValue(IBufferWriter<byte> writer, LogicalField field, LayoutProcessorContext context);
    void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context);

    void BeginRead(ReadOnlySequence<byte> reader, LayoutProcessorContext context);
    bool TryReadValue(ReadOnlySequence<byte> reader, out LogicalField field, LayoutProcessorContext context);
    void EndRead(ReadOnlySequence<byte> reader, LayoutProcessorContext context);
}

// Stream pipeline stage: framing, compression, encryption, etc.
public interface IStreamProcessor : IProcessor
{
    void Encode(ReadOnlySequence<byte> input, IBufferWriter<byte> output, StreamProcessorContext context);
    void Decode(ReadOnlySequence<byte> input, IBufferWriter<byte> output, StreamProcessorContext context);
}

public sealed class NullValueProcessor : IValueProcessor
{
    public string Id => "null-value";
    public string Name => "Null Value Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public object Encode(object logicalValue, ValueProcessorContext context) => logicalValue;
    public object Decode(object physicalValue, ValueProcessorContext context) => physicalValue;
}

public sealed class NullFieldProcessor : IFieldProcessor
{
    public string Id => "null-field";
    public string Name => "Null Field Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public EncodedField EncodeField(LogicalField field, FieldProcessorContext context)
        => new EncodedField { Name = field.Name, BitWidth = 0, RawBits = 0 };
    public LogicalField DecodeField(EncodedField field, FieldProcessorContext context)
        => new LogicalField { Name = field.Name, LogicalType = typeof(object), Value = null };
}

public sealed class NullLayoutProcessor : ILayoutProcessor
{
    public string Id => "null-layout";
    public string Name => "Null Layout Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
    public void WriteValue(IBufferWriter<byte> writer, LogicalField field, LayoutProcessorContext context)
    {
    }
    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
    public void BeginRead(ReadOnlySequence<byte> reader, LayoutProcessorContext context) { }
    public bool TryReadValue(ReadOnlySequence<byte> reader, out LogicalField field, LayoutProcessorContext context)
    {
        field = new LogicalField { Name = "", LogicalType = typeof(object), Value = null };
        return false;
    }
    public void EndRead(ReadOnlySequence<byte> reader, LayoutProcessorContext context) { }
}

public sealed class NullStreamProcessor : IStreamProcessor
{
    public string Id => "null-stream";
    public string Name => "Null Stream Processor";
    public PipelineStage Stage => PipelineStage.Stream;
    public void Encode(ReadOnlySequence<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
    {
        foreach (var segment in input)
        {
            output.Write(segment.Span);
        }
    }
    public void Decode(ReadOnlySequence<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
    {
        foreach (var segment in input)
        {
            output.Write(segment.Span);
        }
    }
}

public sealed class LogicalField
{
    public required string Name { get; init; }
    public required Type LogicalType { get; init; }
    public required object? Value { get; init; }
}

public sealed class EncodedField
{
    public required string Name { get; init; }
    public required int BitWidth { get; init; }
    public required ulong RawBits { get; init; }
}
