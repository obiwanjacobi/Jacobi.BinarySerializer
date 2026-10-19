using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Lays out fields as whole bytes in declaration order, in the configured byte order.
/// Group property 'endian' ('little' or 'big') sets the byte order of fixed-width values (default: little, the canonical form).
/// Values without a fixed width (strings) are passed on unchanged.
/// Usable as the innermost layout processor or as an outer byte wrapper: fixed-width field bytes are converted in place.
/// </summary>
internal sealed class BytePackerProcessor : ProcessorBase, ILayoutProcessor,
    ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
{
    private static readonly PropertyDescriptor EndianProperty = new("byteorder", "sys.endianness", false, description: "Group: 'little' (default) or 'big' byte order of fixed-width values.");

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => DefaultProcessors.LayoutProcessor.BeginWrite(writer, context);

    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        if (encodedValue.Value is byte[] bytes && IsFixedWidth(context))
        {
            var converted = EndianCodec.Convert(bytes, Endianness.Little, GetEndianness(context));
            encodedValue = encodedValue with { Value = converted };
            context.Logger.ByteOrderConverted(GetPath(context), GetEndianness(context).ToString());
        }

        return DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => DefaultProcessors.LayoutProcessor.EndWrite(writer, context);

    public LayoutReadResult BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => DefaultProcessors.LayoutProcessor.BeginRead(ref reader, context);

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        if (result.Status == ReadResult.Success && result.Value?.Value is byte[] bytes && IsFixedWidth(context)
            && (context.InputByteLength is null || bytes.Length == context.FixedByteLength))
        {
            var converted = EndianCodec.Convert(bytes, GetEndianness(context), Endianness.Little);
            context.Logger.ByteOrderConverted(GetPath(context), GetEndianness(context).ToString());
            return LayoutReadResult<EncodedField>.Success(result.Value with { Value = converted });
        }

        return result;
    }

    public LayoutReadResult EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => DefaultProcessors.LayoutProcessor.EndRead(ref reader, context);

    // chained
    public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
    {
        if (IsFixedWidth(context) && bytes.Length == context.FixedByteLength)
        {
            var converted = EndianCodec.Convert(bytes.ToArray(), Endianness.Little, GetEndianness(context));
            context.Logger.ByteOrderConverted(GetPath(context), GetEndianness(context).ToString());
            writer.Write(converted);
            return WriteResult.Success;
        }

        return LayoutChain.PassThroughWrite(writer, bytes);
    }

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = LayoutChain.PassThroughRead(ref reader, context.InputByteLength);
        if (result.Status != ReadResult.Success || !IsFixedWidth(context)
            || result.Value.Length != context.FixedByteLength)
        {
            return result;
        }

        var converted = EndianCodec.Convert(result.Value.ToArray(), GetEndianness(context), Endianness.Little);
        context.Logger.ByteOrderConverted(GetPath(context), GetEndianness(context).ToString());
        return LayoutReadResult<ReadOnlyMemory<byte>>.Success(converted);
    }

    private static string GetPath(LayoutProcessorContext context)
        => context.Field?.Path.ToString() ?? String.Empty;

    private static bool IsFixedWidth(LayoutProcessorContext context)
        => context.FixedByteLength is > 1;

    private static Endianness GetEndianness(LayoutProcessorContext context)
    {
        if (context.Properties.TryGet<Endianness>(EndianProperty, out var endianness))
        {
            return endianness;
        }

        for (var group = context.Group; group is not null; group = group.Parent as GroupInfo)
        {
            if (context.PropertiesOf(group.Group.Properties).TryGet(EndianProperty, out endianness))
            {
                return endianness;
            }
        }

        return Endianness.Little;
    }

    public ProcessorKey Key => new("sys.bytepacker");
    public string Name => "Byte-Packer Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties => [EndianProperty];
}
