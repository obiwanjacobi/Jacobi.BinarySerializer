using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Packs the logical values of a packed group into one carrier, in declaration order, using arbitrary bit widths.
/// Child field property 'bits' sets the width (default: 1).
/// Processor property 'bitorder' ('little' or 'big') sets the bit order (default: little).
/// The carrier has the byte length of the packed group: unused trailing bits are zero (and ignored when reading).
/// </summary>
internal sealed class BitPackerProcessor : ProcessorBase, IPackingProcessor
{
    private static readonly PropertyDescriptor BitsProperty = new("bits", "sys.int32", false, description: "Field: the number of bits the field occupies (default: 1).");
    private static readonly PropertyDescriptor BitOrderProperty = new("bitorder", "sys.endianness", false, description: "Packed group: 'little' (default) or 'big' bit order within a byte.");

    public FieldWriteResult<EncodedField> Write(IReadOnlyList<LogicalField> fields, PackingProcessorContext context)
    {
        if (!TryGetWidths(context, out var widths) || fields.Count != widths.Length)
        {
            return FieldWriteResult<EncodedField>.Failure();
        }

        var buffer = new ArrayBufferWriter<byte>(context.ByteLength);
        var writer = new BitWriter(buffer, GetBitOrder(context));

        for (var i = 0; i < widths.Length; i++)
        {
            var info = context.Fields[i];
            var bits = widths[i];
            if (info.DataType.Encode is not { } encode || !encode(fields[i].Value, out var bytes) || bytes.Length != info.DataType.FixedSize)
            {
                return Reject(context, info, "the value cannot be encoded");
            }

            ulong value = 0;
            for (var b = bytes.Length - 1; b >= 0; b--)
            {
                value = (value << 8) | bytes[b];
            }

            if (!Fits(value, bytes.Length * 8, bits, IsSigned(info)))
            {
                return Reject(context, info, $"the value does not fit in {bits} bits");
            }

            writer.Write(value, bits);
            context.Logger.BitsPacked(info.Path, bits);
        }

        writer.Flush();
        var carrier = new byte[context.ByteLength];
        buffer.WrittenSpan.CopyTo(carrier);
        return FieldWriteResult<EncodedField>.Written(new(context.Group.Name, typeof(byte[]), carrier, carrier.Length * 8), carrier.Length * 8);
    }

    public FieldReadResult<IReadOnlyList<LogicalField>> Read(EncodedField carrier, PackingProcessorContext context)
    {
        if (carrier.Value is not byte[] bytes || bytes.Length != context.ByteLength || !TryGetWidths(context, out var widths))
        {
            return FieldReadResult<IReadOnlyList<LogicalField>>.Failure();
        }

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
        var bitReader = new BitReader(GetBitOrder(context));
        var fields = new List<LogicalField>(widths.Length);

        for (var i = 0; i < widths.Length; i++)
        {
            var info = context.Fields[i];
            var bits = widths[i];
            var size = info.DataType.FixedSize!.Value;

            if (!bitReader.TryRead(ref reader, bits, out var value))
            {
                return FieldReadResult<IReadOnlyList<LogicalField>>.Failure();
            }

            if (IsSigned(info) && bits < 64 && (value & (1UL << (bits - 1))) != 0)
            {
                value |= ~BitWriter.Mask(bits);
            }

            var encoded = new byte[size];
            for (var b = 0; b < size; b++)
            {
                encoded[b] = (byte)(value >> (8 * b));
            }

            if (info.DataType.Decode is not { } decode || !decode(encoded, out var logical))
            {
                context.Logger.BitsRejected(info.Path, "the value cannot be decoded");
                return FieldReadResult<IReadOnlyList<LogicalField>>.Failure();
            }

            fields.Add(new LogicalField(info.Name, info.DataType.ClrType, logical));
            context.Logger.BitsUnpacked(info.Path, bits);
        }

        return FieldReadResult<IReadOnlyList<LogicalField>>.Consumed(fields, bytes.Length * 8);
    }

    private static FieldWriteResult<EncodedField> Reject(PackingProcessorContext context, FieldInfo field, string reason)
    {
        context.Logger.BitsRejected(field.Path, reason);
        return FieldWriteResult<EncodedField>.Failure();
    }

    /// <summary>Resolves the bit width of every child (1 to the width of its data type, at most 64); all must fit in the carrier.</summary>
    private static bool TryGetWidths(PackingProcessorContext context, out int[] widths)
    {
        widths = new int[context.Fields.Count];
        var total = 0;
        for (var i = 0; i < widths.Length; i++)
        {
            var info = context.Fields[i];
            if (info.DataType.FixedSize is not { } size || size > 8)
            {
                context.Logger.BitsRejected(info.Path, "the data type is not 1 to 8 bytes wide");
                return false;
            }

            var bits = context.PropertiesOf(info.Field.Properties).GetOrDefault(BitsProperty, 1);
            if (bits < 1 || bits > size * 8)
            {
                context.Logger.BitsRejected(info.Path, $"{bits} bits is out of range");
                return false;
            }

            widths[i] = bits;
            total += bits;
        }

        if (total > context.ByteLength * 8)
        {
            context.Logger.BitsRejected(context.Group.Path, $"{total} bits do not fit in {context.ByteLength} byte(s)");
            return false;
        }

        return true;
    }

    private static bool Fits(ulong value, int storedBits, int bits, bool signed)
    {
        if (bits >= storedBits)
        {
            return true;
        }

        var upper = value >> bits;
        if (!signed)
        {
            return upper == 0;
        }

        // sign-extended: all the bits above the width (up to the stored width) must equal the sign bit.
        var upperMask = BitWriter.Mask(storedBits - bits);
        var signBit = (value >> (bits - 1)) & 1;
        return upper == (signBit == 1 ? upperMask : 0);
    }

    private static bool IsSigned(FieldInfo field)
        => field.DataType.ClrType == typeof(sbyte) || field.DataType.ClrType == typeof(short)
            || field.DataType.ClrType == typeof(int) || field.DataType.ClrType == typeof(long);

    private static Endianness GetBitOrder(PackingProcessorContext context)
    {
        if (context.Properties.TryGet<Endianness>(BitOrderProperty, out var order))
        {
            return order;
        }

        return context.PropertiesOf(context.Group.Group.Properties).GetOrDefault(BitOrderProperty, Endianness.Little);
    }

    public ProcessorKey Key => new("sys.bitpacker");
    public string Name => "Bit-Packer Processor";
    public PipelineStage Stage => PipelineStage.Packing;
    public IReadOnlyList<PropertyDescriptor> Properties => [BitsProperty, BitOrderProperty];
}
