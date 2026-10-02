using System.Buffers;

namespace Jacobi.BinarySerializer.Processor;

public interface IProcessor
{
    /// <summary>
    /// A unique identifier for the processor, used in schema definitions.
    /// </summary>
    ProcessorKey Key { get; }
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

public sealed record LogicalField(string Name, Type LogicalType, object? Value);
public sealed record EncodedField(string Name, Type PhysicalType, object? Value, int BitWidth);
