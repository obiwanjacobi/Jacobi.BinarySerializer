using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Lays out fields as whole bytes in declaration order, in the configured byte order.
/// Group property 'endian' ('little' or 'big') sets the byte order of fixed-width values (default: little, the canonical form).
/// Values without a fixed width (strings) are passed on unchanged.
/// </summary>
internal sealed class BytePackerProcessor : ILayoutProcessor
{
    private const string EndianProperty = "byteorder";

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => ProcessorDefaults.DefaultLayoutProcessor.BeginWrite(writer, context);

    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        if (encodedValue.Value is byte[] bytes && IsFixedWidth(context))
        {
            var converted = EndianCodec.Convert(bytes, Endianness.Little, GetEndianness(context));
            encodedValue = encodedValue with { Value = converted };
        }

        return ProcessorDefaults.DefaultLayoutProcessor.Write(writer, encodedValue, context);
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => ProcessorDefaults.DefaultLayoutProcessor.EndWrite(writer, context);

    public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => ProcessorDefaults.DefaultLayoutProcessor.BeginRead(ref reader, context);

    public ReadResult Read(ref SequenceReader<byte> reader, out EncodedField encodedValue, LayoutProcessorContext context)
    {
        var result = ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, out encodedValue, context);
        if (result == ReadResult.Success && encodedValue.Value is byte[] bytes && IsFixedWidth(context))
        {
            var converted = EndianCodec.Convert(bytes, GetEndianness(context), Endianness.Little);
            encodedValue = encodedValue with { Value = converted };
        }

        return result;
    }

    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => ProcessorDefaults.DefaultLayoutProcessor.EndRead(ref reader, context);

    private static bool IsFixedWidth(LayoutProcessorContext context)
        => context.Field is not null && DataTypeCodec.FixedSize(context.Field.Field.Type) is > 1;

    private static Endianness GetEndianness(LayoutProcessorContext context)
    {
        var property = context.ProcessorProperties
            .FirstOrDefault(p => p.Name.Equals(EndianProperty, StringComparison.OrdinalIgnoreCase))
            ?? context.Group?.Group.Properties
                .FirstOrDefault(p => p.Name.Equals(EndianProperty, StringComparison.OrdinalIgnoreCase));

        if (property is null)
        {
            return Endianness.Little;
        }

        return property.Value.ToLowerInvariant() switch
        {
            "little" => Endianness.Little,
            "big" => Endianness.Big,
            _ => throw new InvalidOperationException($"Invalid '{EndianProperty}' value '{property.Value}'. Expected 'little' or 'big'.")
        };
    }

    public ProcessorKey Key => new("sys:bytepacker");
    public string Name => "Byte-Packer Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties =>
    [
        new(EndianProperty, typeof(string), false, description: "Group: 'little' (default) or 'big' byte order of fixed-width values."),
    ];
}
