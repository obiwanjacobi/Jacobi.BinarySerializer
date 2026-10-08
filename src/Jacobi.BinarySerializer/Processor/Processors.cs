using System.Buffers;
using Jacobi.BinarySerializer.Schema;

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

    /// <summary>
    /// Publishes the properties a processor supports.
    /// </summary>
    public IReadOnlyList<PropertyDescriptor> Properties { get; }
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
    /// <summary>The result states how many bits the encoded value occupies; the engine uses that as the field's BitWidth.</summary>
    FieldWriteResult<OutT> Write(InT field, FieldProcessorContext context);
}
public interface IFieldReader<InT, OutT> : IProcessor
{
    /// <summary>The result states how many bits of the provided encoded value were consumed, so a processor cannot forget to.</summary>
    FieldReadResult<OutT> Read(InT field, FieldProcessorContext context);
}

/// <summary>The outcome of a field write: the value and the number of bits it occupies.</summary>
public readonly record struct FieldWriteResult<T>(WriteResult Status, T Value, int BitsWritten)
{
    public static FieldWriteResult<T> Written(T value, int bitsWritten) => new(WriteResult.Success, value, bitsWritten);
    public static FieldWriteResult<T> Failure() => new(WriteResult.Failure, default!, 0);
}

/// <summary>
/// The outcome of a field read:
/// A fixed-width field must consume exactly its width; an open-width field (e.g. varint) is offered a window
/// (see <see cref="DefaultLayoutProcessor.OpenWidthWindowBytes"/>) and the engine gives back the bytes that were not consumed.
/// </summary>
public readonly record struct FieldReadResult<T>(ReadResult Status, T Value, int BitsConsumed)
{
    public static FieldReadResult<T> Consumed(T value, int bitsConsumed) => new(ReadResult.Success, value, bitsConsumed);
    public static FieldReadResult<T> NeedMoreData() => new(ReadResult.NeedMoreData, default!, 0);
    public static FieldReadResult<T> Failure() => new(ReadResult.Failure, default!, 0);
}

// Layout pipeline stage: bit packing, alignment, endian conversion, etc.
// Every layout processor is a chain head (ILayoutProcessor: field <-> bytes).
// A processor that can also FOLLOW another one in a chain additionally implements the byte-to-byte variants
// ILayoutWriter<ReadOnlySpan<byte>> and ILayoutReader<ReadOnlyMemory<byte>> (the plan builder checks this).
// Chain order = declaration order: the first processor is the head, the last one writes to / reads from the actual stream.
// The engine owns the buffers between the stages; processors stay stateless.
// Write: Begin runs last-to-first, End runs first-to-last; the bytes a stage emits (also in Begin/End) flow through the later stages.
// Read: Begin/End work on the actual reader (same order as write). A chained field Read gets a reader over the unread input:
//       it consumes the bytes it owns (e.g. padding) and returns the rest (see LayoutChain.Unread) for the next stage; keep the length.
// Context.RootPosition / GroupPosition tell where in the actual stream the call happens.
public interface ILayoutProcessor : ILayoutWriter<EncodedField>, ILayoutReader<EncodedField> { }
public interface ILayoutWriter<InT> : IProcessor where InT : allows ref struct
{
    void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context);
    WriteResult Write(IBufferWriter<byte> writer, InT encodedValue, LayoutProcessorContext context);
    void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context);
}
public interface ILayoutReader<OutT> : IProcessor
{
    void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context);
    LayoutReadResult<OutT> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context);
    void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context);
}

/// <summary>The outcome of a layout read: the value, or a status why there is none. Widths are measured by the engine (reader.Consumed), not reported.</summary>
public readonly record struct LayoutReadResult<T>(ReadResult Status, T Value)
{
    public static LayoutReadResult<T> Success(T value) => new(ReadResult.Success, value);
    public static LayoutReadResult<T> NeedMoreData() => new(ReadResult.NeedMoreData, default!);
    public static LayoutReadResult<T> EndOfData() => new(ReadResult.EndOfData, default!);
    public static LayoutReadResult<T> Failure() => new(ReadResult.Failure, default!);
    public static LayoutReadResult<T> WithStatus(ReadResult status) => new(status, default!);
}

// Stream pipeline stage: framing, compression, encryption, etc.
public interface IStreamProcessor : IProcessor
{
    WriteResult Write(ref SequenceReader<byte> payloadInput, IBufferWriter<byte> transportOutput, StreamProcessorContext context);
    ReadResult Read(ref SequenceReader<byte> transportInput, IBufferWriter<byte> payloadOutput, StreamProcessorContext context);
}

public sealed class PropertyDescriptor
{
    public PropertyDescriptor(string name, SchemaName dataType)
    {
        Name = name;
        DataType = dataType;
        Description = String.Empty;
    }

    public PropertyDescriptor(string name, SchemaName dataType, bool isRequired, bool isReadOnly = false, bool isPublished = false, string? description = null)
    {
        Name = name;
        DataType = dataType;
        IsRequired = isRequired;
        IsReadOnly = isReadOnly;
        IsPublished = isPublished;
        Description = description ?? String.Empty;
    }

    public string Name { get; }
    /// <summary>
    /// The name of the registered data type of the property value (for example 'sys.int32').
    /// </summary>
    public SchemaName DataType { get; }

    public bool IsRequired { get; }
    public bool IsReadOnly { get; }
    public bool IsPublished { get; }

    public string Description { get; }
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

public readonly record struct ProcessorKey
{
    public const char Separator = '.';

    public ProcessorKey(string fullname)
    {
        var i = fullname.LastIndexOf(Separator);
        if (i < 0)
        {
            throw new ArgumentException($"Invalid processor fullname '{fullname}'. Expected format 'namespace{Separator}id'.");
        }

        Namespace = fullname[..i];
        Id = fullname[(i + 1)..];
    }

    public ProcessorKey(string @namespace, string id)
    {
        Namespace = @namespace;
        Id = id;
    }

    public string Id { get; init; }
    public string Namespace { get; init; }

    /// <summary>The full property name: 'namespace.id.name'.</summary>
    public string PropertyName(string name)
        => $"{Namespace}{Separator}{Id}{Separator}{name}";

    public override string ToString() => $"{Namespace}{Separator}{Id}";
}
