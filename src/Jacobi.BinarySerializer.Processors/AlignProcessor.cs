using System.Buffers;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Pads with zero bytes so that each value, and the end of the group, starts on a multiple of 'bytes'.
/// Processor property 'bytes' (required, &gt; 0) is the alignment; 'relative' ('group' (default) or 'root') is what the position is relative to.
/// Usable as the innermost layout processor or as an outer byte wrapper (e.g. around the byte packer).
/// </summary>
/// <remarks>
/// As an outer wrapper, alignment is applied only when the inner processor emits or requests bytes.
/// </remarks>
internal sealed class AlignProcessor : ProcessorBase, ILayoutProcessor,
    ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
{
    public static readonly SchemaName RelativeDataType = "sys.alignrelativeto";
    private static readonly PropertyDescriptor BytesProperty = new("bytes", "sys.int32", true, description: "The alignment in bytes.");
    private static readonly PropertyDescriptor RelativeProperty = new("relative", RelativeDataType, false, description: "'group' (default) or 'root': what the position is relative to.");

    public override IEnumerable<DataTypeDescriptor> DataTypes => [DataTypeDescriptor.ForEnum<AlignRelativeTo>(RelativeDataType)];

    public ProcessorKey Key => new("sys.align");
    public string Name => "Align Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties => [BytesProperty, RelativeProperty];

    // head
    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
    }

    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        Pad(writer, context);
        return DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => Pad(writer, context);

    public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => LayoutReadResult<EncodedField>.Success();

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = BeginRead(ref reader, context);
        return new(result.Status, RequestedByteCount: result.RequestedByteCount);
    }

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var padding = ReadPadding(context);
        if (!TrySkip(ref reader, context))
        {
            var length = context.FieldData.ByteLength
                ?? (context.Field?.Pipeline.FieldProcessors.Count > 0 ? context.InputByteLength : context.FixedByteLength)
                ?? 1;
            return LayoutReadResult<EncodedField>.NeedMoreData(padding + length);
        }
        var result = DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        return result.Status == ReadResult.NeedMoreData
            ? LayoutReadResult<EncodedField>.NeedMoreData(padding + (result.RequestedByteCount ?? 1))
            : result;
    }

    public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var padding = ReadPadding(context);
        if (!TrySkip(ref reader, context))
        {
            return LayoutReadResult<EncodedField>.NeedMoreData(padding);
        }
        return LayoutReadResult<EncodedField>.Success();
    }

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = EndRead(ref reader, context);
        return new(result.Status, RequestedByteCount: result.RequestedByteCount);
    }

    // chained
    public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
    {
        Pad(writer, context);
        writer.Write(bytes);
        return WriteResult.Success;
    }

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var padding = ReadPadding(context);
        if (!TrySkip(ref reader, context))
        {
            return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData(padding + (context.InputByteLength ?? 1));
        }
        var result = LayoutChain.PassThroughRead(ref reader, context.InputByteLength);
        return result.Status == ReadResult.NeedMoreData
            ? LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData(padding + (result.RequestedByteCount ?? 1))
            : result;
    }

    private static bool TrySkip(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var padding = ReadPadding(context);
        if (reader.Remaining < padding)
        {
            context.Logger.AlignNeedMoreData(padding);
            return false;
        }
        reader.Advance(padding);
        return true;
    }

    private static void Pad(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var padding = (int)Padding(context);
        if (padding == 0)
        {
            return;
        }

        writer.GetSpan(padding).Slice(0, padding).Clear();
        writer.Advance(padding);
        context.Logger.AlignPadded(padding, GetAlignment(context), IsRoot(context) ? context.GroupData.RootPosition : context.GroupData.Position);
    }

    private static long Padding(LayoutProcessorContext context)
    {
        var alignment = GetAlignment(context);
        var position = IsRoot(context) ? context.GroupData.RootPosition : context.GroupData.Position;
        return (alignment - position % alignment) % alignment;
    }

    private static int ReadPadding(LayoutProcessorContext context)
        => context.IsReadContinuation ? 0 : (int)Padding(context);

    private static int GetAlignment(LayoutProcessorContext context)
    {
        var alignment = context.Properties.Get<int>(BytesProperty);
        if (alignment <= 0)
        {
            throw context.Logger.Fail($"Invalid '{BytesProperty.Name}' value '{alignment}'. Expected a positive integer.");
        }
        return alignment;
    }

    private static bool IsRoot(LayoutProcessorContext context)
        => context.Properties.GetOrDefault(RelativeProperty, AlignRelativeTo.Group) == AlignRelativeTo.Root;
}

internal enum AlignRelativeTo
{
    Group,
    Root
}
