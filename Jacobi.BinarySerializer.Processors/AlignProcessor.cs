using System.Buffers;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Pads with zero bytes so that each value, and the end of the group, starts on a multiple of 'bytes'.
/// Processor property 'bytes' (required, &gt; 0) is the alignment; 'relative' ('group' (default) or 'root') is what the position is relative to.
/// Usable as the layout head, or chained after a head (e.g. the byte packer).
/// </summary>
/// <remarks>
/// Chained after a bit packer, only the bytes the packer emits are aligned. On read, the aligner cannot tell which fields
/// consumed bits only, so combine them with care.
/// </remarks>
internal sealed class AlignProcessor : ILayoutProcessor,
    ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
{
    private const string BytesProperty = "bytes";
    private const string RelativeProperty = "relative";

    public ProcessorKey Key => new("sys.align");
    public string Name => "Align Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties =>
    [
        new(BytesProperty, typeof(int), true, description: "The alignment in bytes."),
        new(RelativeProperty, typeof(string), false, description: "'group' (default) or 'root': what the position is relative to."),
    ];

    // head
    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
    }

    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        Pad(writer, context);
        return ProcessorDefaults.DefaultLayoutProcessor.Write(writer, encodedValue, context);
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => Pad(writer, context);

    public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
    }

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        if (!TrySkip(ref reader, context))
        {
            return LayoutReadResult<EncodedField>.NeedMoreData();
        }
        return ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, context);
    }

    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        // a trailing pad that is not there (yet) is not an error here: the reader reports NeedMoreData when it needs the bytes.
        var padding = (int)Math.Min(Padding(context), reader.Remaining);
        reader.Advance(padding);
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
        if (!TrySkip(ref reader, context))
        {
            return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData();
        }
        return LayoutReadResult<ReadOnlyMemory<byte>>.Success(LayoutChain.Unread(reader));
    }

    private static bool TrySkip(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var padding = Padding(context);
        if (reader.Remaining < padding)
        {
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
    }

    private static long Padding(LayoutProcessorContext context)
    {
        var alignment = GetAlignment(context);
        var position = IsRoot(context) ? context.RootPosition : context.GroupPosition;
        return (alignment - position % alignment) % alignment;
    }

    private static int GetAlignment(LayoutProcessorContext context)
    {
        var property = context.Properties.Find(BytesProperty)
            ?? throw new InvalidOperationException($"Missing required '{BytesProperty}' property on the align processor.");
        if (!Int32.TryParse(property.Value, out var alignment) || alignment <= 0)
        {
            throw new InvalidOperationException($"Invalid '{BytesProperty}' value '{property.Value}'. Expected a positive integer.");
        }
        return alignment;
    }

    private static bool IsRoot(LayoutProcessorContext context)
    {
        var property = context.Properties.Find(RelativeProperty);

        if (property is null)
        {
            return false;
        }

        return property.Value.ToLowerInvariant() switch
        {
            "group" => false,
            "root" => true,
            _ => throw new InvalidOperationException($"Invalid '{RelativeProperty}' value '{property.Value}'. Expected 'group' or 'root'.")
        };
    }
}
