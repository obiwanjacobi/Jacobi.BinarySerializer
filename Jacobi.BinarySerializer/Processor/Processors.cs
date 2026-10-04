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
    OutT Write(InT field, FieldProcessorContext context);
}
public interface IFieldReader<InT, OutT> : IProcessor
{
    OutT Read(InT field, FieldProcessorContext context);
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
    ReadResult Read(ref SequenceReader<byte> reader, out OutT outValue, LayoutProcessorContext context);
    void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context);
}

// Stream pipeline stage: framing, compression, encryption, etc.
public interface IStreamProcessor : IProcessor
{
    WriteResult Write(ref SequenceReader<byte> payloadInput, IBufferWriter<byte> transportOutput, StreamProcessorContext context);
    ReadResult Read(ref SequenceReader<byte> transportInput, IBufferWriter<byte> payloadOutput, StreamProcessorContext context);
}

public sealed class PropertyDescriptor
{
    public PropertyDescriptor(string name, Type type)
    {
        Name = name;
        PropertyType = type;
        Description = String.Empty;
    }

    public PropertyDescriptor(string name, Type type, bool isRequired, bool isReadOnly = false, bool isPublished = false, string? description = null)
    {
        Name = name;
        PropertyType = type;
        IsRequired = isRequired;
        IsReadOnly = isReadOnly;
        IsPublished = isPublished;
        Description = description ?? String.Empty;
    }

    public string Name { get; }
    public Type PropertyType { get; }

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
    public ProcessorKey(string fullname)
    {
        var i = fullname.LastIndexOf(':');
        if (i < 0)
        {
            throw new ArgumentException($"Invalid processor fullname '{fullname}'. Expected format 'namespace:id'.");
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

    /// <summary>The full property name: 'namespace:id.name'.</summary>
    public string PropertyName(string name) => $"{Namespace}:{Id}.{name}";
}
