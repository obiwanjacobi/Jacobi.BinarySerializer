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

    public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
    {
        var type = context.Field.DataType;
        if (type.Encode is not { } encode || !encode(field.Value, out var bytes))
        {
            throw new InvalidOperationException(
                $"'{context.Field.Path}': cannot encode value '{field.Value ?? "null"}' as {type}.");
        }

        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, bytes.Length * 8), bytes.Length * 8);
    }

    public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
    {
        var type = context.Field.DataType;
        if (field.Value is not byte[] bytes || type.Decode is not { } decode || !decode(bytes, out var value))
        {
            throw new InvalidOperationException($"'{context.Field.Path}': cannot decode the encoded value as {type}.");
        }

        return FieldReadResult<LogicalField>.Consumed(
            new(field.Name, type.ClrType, value), bytes.Length * 8);
    }
}

/// <summary>Pass-through: forwards already-encoded bytes unchanged. The session skips empty stages, so this is only used when asked for explicitly.</summary>
public sealed class DefaultLayoutProcessor : ILayoutProcessor
{
    /// <summary>The most bytes offered to a field processor that decides its own width (enough for a 64-bit varint).</summary>
    public const int OpenWidthWindowBytes = 10;

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
    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var field = context.Field;
        var name = field?.Name ?? string.Empty;

        if (field is not null && context.FieldData.ByteLength is { } length)
        {
            if (reader.Remaining < length)
            {
                return LayoutReadResult<EncodedField>.NeedMoreData();
            }

            var blob = new byte[length];
            reader.TryCopyTo(blob);
            reader.Advance(length);
            return LayoutReadResult<EncodedField>.Success(new EncodedField(name, typeof(byte[]), blob, length * 8));
        }

        if (field is not null && field.Pipeline.FieldProcessors.Count > 0)
        {
            // The field processor decides how much of the window it uses (it reports that in its read result);
            // the engine gives the unused bytes back to the reader.
            var window = (int)Math.Min(context.FieldData.OpenWidthWindowBytes, reader.Remaining);
            if (window == 0)
            {
                return LayoutReadResult<EncodedField>.NeedMoreData();
            }

            var windowBytes = new byte[window];
            reader.TryCopyTo(windowBytes);
            reader.Advance(window);
            return LayoutReadResult<EncodedField>.Success(new EncodedField(name, typeof(byte[]), windowBytes, window * 8));
        }

        if (field is not null && field.DataType.FixedSize is { } size)
        {
            if (reader.Remaining < size)
            {
                return LayoutReadResult<EncodedField>.NeedMoreData();
            }

            var bytes = new byte[size];
            reader.TryCopyTo(bytes);
            reader.Advance(size);
            return LayoutReadResult<EncodedField>.Success(new EncodedField(name, typeof(byte[]), bytes, size * 8));
        }

        // variable-width fields without a field processor (String) get all unread bytes as one value; use sys:string to delimit them.
        var remaining = reader.UnreadSequence.ToArray();
        reader.Advance(remaining.Length);
        return LayoutReadResult<EncodedField>.Success(new EncodedField(name, typeof(byte[]), remaining, remaining.Length * 8));
    }
    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
}

/// <summary>Pass-through: copies the unread input to the output unchanged.
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
