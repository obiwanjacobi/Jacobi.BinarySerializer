using System.Buffers;
using Jacobi.BinarySerializer.Codecs;

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

    public IReadOnlyList<PropertyDescriptor> Properties => [];

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
        => logicalValue;

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
        => logicalValue;
}

/// <summary>Fixed-width (little-endian) representation of the field's schema data type, see <see cref="DataTypeCodec"/>.</summary>
public sealed class DefaultFieldProcessor : IFieldProcessor
{
    public ProcessorKey Key => new("default", "field");
    public string Name => "Default Field Processor";
    public PipelineStage Stage => PipelineStage.Representation;

    public IReadOnlyList<PropertyDescriptor> Properties => [];

    public EncodedField Write(LogicalField field, FieldProcessorContext context)
    {
        var type = context.Field.Field.Type;
        if (!DataTypeCodec.TryEncode(type, field.Value, out var bytes))
        {
            throw new InvalidOperationException(
                $"'{context.Field.Path}': cannot encode value '{field.Value ?? "null"}' as {type}.");
        }

        return new(field.Name, typeof(byte[]), bytes, bytes.Length * 8);
    }

    public LogicalField Read(EncodedField field, FieldProcessorContext context)
    {
        var type = context.Field.Field.Type;
        if (field.Value is not byte[] bytes || !DataTypeCodec.TryDecode(type, bytes, out var value))
        {
            throw new InvalidOperationException($"'{context.Field.Path}': cannot decode the encoded value as {type}.");
        }

        return new(field.Name, DataTypeCodec.ClrType(type) ?? typeof(object), value);
    }
}

/// <summary>Pass-through: forwards already-encoded bytes unchanged. The session skips empty stages, so this is only used when asked for explicitly.</summary>
public sealed class DefaultLayoutProcessor : ILayoutProcessor
{
    public ProcessorKey Key => new("default", "layout");
    public string Name => "Default Layout Processor";
    public PipelineStage Stage => PipelineStage.Layout;

    public IReadOnlyList<PropertyDescriptor> Properties => [];

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        switch (encodedValue.Value)
        {
            case null:
                return WriteResult.Success;
            case byte[] bytes:
                writer.Write(bytes);
                return WriteResult.Success;
            case ReadOnlyMemory<byte> memory:
                writer.Write(memory.Span);
                return WriteResult.Success;
            case Memory<byte> memory:
                writer.Write(memory.Span);
                return WriteResult.Success;
            default:
                // a pass-through cannot lay out a value that is not already bytes.
                return WriteResult.Failure;
        }
    }
    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }

    public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
    public ReadResult Read(ref SequenceReader<byte> reader, out EncodedField encodedValue, LayoutProcessorContext context)
    {
        var field = context.Field;
        var name = field?.Name ?? string.Empty;

        if (field is not null && DataTypeCodec.FixedSize(field.Field.Type) is { } size)
        {
            if (reader.Remaining < size)
            {
                encodedValue = new EncodedField(name, typeof(byte[]), null, 0);
                return ReadResult.NeedMoreData;
            }

            var bytes = new byte[size];
            reader.TryCopyTo(bytes);
            reader.Advance(size);
            encodedValue = new EncodedField(name, typeof(byte[]), bytes, size * 8);
            return ReadResult.Success;
        }

        // TODO: variable-width fields (String) need length info; all unread bytes are passed on as one value.
        // See if there is a length property in the FieldInfo.
        var remaining = reader.UnreadSequence.ToArray();
        reader.Advance(remaining.Length);
        encodedValue = new EncodedField(name, typeof(byte[]), remaining, remaining.Length * 8);
        return ReadResult.Success;
    }
    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
}

/// <summary>Pass-through: copies the unread input to the output unchanged. The session skips empty stages, so this is only used when asked for explicitly.</summary>
public sealed class DefaultStreamProcessor : IStreamProcessor
{
    public ProcessorKey Key => new("default", "stream");
    public string Name => "Default Stream Processor";
    public PipelineStage Stage => PipelineStage.Stream;

    public IReadOnlyList<PropertyDescriptor> Properties => [];

    public WriteResult Write(ref SequenceReader<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
    {
        CopyUnread(ref input, output);
        return WriteResult.Success;
    }
    public ReadResult Read(ref SequenceReader<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
    {
        CopyUnread(ref input, output);
        return ReadResult.Success;
    }

    private static void CopyUnread(ref SequenceReader<byte> input, IBufferWriter<byte> output)
    {
        foreach (var segment in input.UnreadSequence)
        {
            output.Write(segment.Span);
        }
        input.AdvanceToEnd();
    }
}
