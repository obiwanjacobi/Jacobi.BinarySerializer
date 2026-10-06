using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Packs fields of arbitrary bit widths into bytes, in declaration order.
/// Field property 'bits' sets the width (default: the encoded width). Group property 'Endianness' ('lsb' or 'msb') sets the bit order (default: lsb).
/// A partially filled byte is padded with zero bits at the end of the group.
/// </summary>
internal sealed class BitPackerProcessor : ILayoutProcessor
{
    private const string BitsProperty = "bits";
    private const string BitOrderProperty = "bitorder";

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var state = context.GetOrCreateState<BitPackerState>();
        state.Writer = new BitWriter(writer, GetBitOrder(context));
    }

    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        if (encodedValue.Value is not byte[] bytes || bytes.Length > 8)
        {
            context.Logger.BitsRejected(context.Field?.Path ?? String.Empty, "the value is not 1 to 8 bytes");
            return WriteResult.Failure;
        }

        var state = context.GetOrCreateState<BitPackerState>();
        state.Writer ??= new BitWriter(writer, GetBitOrder(context));

        var bits = GetBits(context, encodedValue.BitWidth);
        if (bits < 1 || bits > bytes.Length * 8)
        {
            context.Logger.BitsRejected(context.Field?.Path ?? String.Empty, $"{bits} bits is out of range");
            return WriteResult.Failure;
        }

        ulong value = 0;
        for (var i = bytes.Length - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        if (!Fits(value, bytes.Length * 8, bits, IsSigned(context.Field)))
        {
            context.Logger.BitsRejected(context.Field?.Path ?? String.Empty, $"the value does not fit in {bits} bits");
            return WriteResult.Failure;
        }

        state.Writer.Write(value, bits);
        context.Logger.BitsPacked(context.Field?.Path ?? String.Empty, bits);
        return WriteResult.Success;
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var state = context.GetOrCreateState<BitPackerState>();
        state.Writer?.Flush();
        state.Writer = null;
    }

    public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var state = context.GetOrCreateState<BitPackerState>();
        state.Reader = new BitReader(GetBitOrder(context));
    }

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var field = context.Field;
        var name = field?.Name ?? String.Empty;

        if (field is null || DataTypeCodec.FixedSize(field.Field.DataType) is not { } size || size > 8)
        {
            return LayoutReadResult<EncodedField>.Failure();
        }

        var bits = GetBits(context, size * 8);
        if (bits < 1 || bits > size * 8)
        {
            return LayoutReadResult<EncodedField>.Failure();
        }

        var state = context.GetOrCreateState<BitPackerState>();
        state.Reader ??= new BitReader(GetBitOrder(context));

        if (!state.Reader.TryRead(ref reader, bits, out var value))
        {
            return reader.Remaining == 0 && state.Reader.PendingBits == 0
                ? LayoutReadResult<EncodedField>.EndOfData()
                : LayoutReadResult<EncodedField>.NeedMoreData();
        }

        if (IsSigned(field) && bits < 64 && (value & (1UL << (bits - 1))) != 0)
        {
            value |= ~BitWriter.Mask(bits);
        }

        var result = new byte[size];
        for (var i = 0; i < size; i++)
        {
            result[i] = (byte)(value >> (8 * i));
        }

        context.Logger.BitsUnpacked(name, bits);
        return LayoutReadResult<EncodedField>.Success(new EncodedField(name, typeof(byte[]), result, bits));
    }

    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var state = context.GetOrCreateState<BitPackerState>();
        state.Reader?.Align();
        state.Reader = null;
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

    private static bool IsSigned(FieldInfo? field)
        => field?.Field.DataType is SchemaDataType.Int8 or SchemaDataType.Int16 or SchemaDataType.Int32 or SchemaDataType.Int64;

    private static int GetBits(LayoutProcessorContext context, int defaultBits)
    {
        var property = context.PropertiesOf(context.Field?.Field.Properties).Find(BitsProperty);
        if (property is null)
        {
            return defaultBits;
        }

        return Int32.TryParse(property.Value, out var bits)
            ? bits
            : throw context.Logger.Fail($"'{context.Field!.Path}': invalid '{BitsProperty}' value '{property.Value}'.");
    }

    private static Endianness GetBitOrder(LayoutProcessorContext context)
    {
        var property = context.Properties.Find(BitOrderProperty)
            ?? context.PropertiesOf(context.Group?.Group.Properties).Find(BitOrderProperty);

        if (property is null)
        {
            return Endianness.Little;
        }

        return property.Value?.ToLowerInvariant() switch
        {
            "lsb" => Endianness.Little,
            "msb" => Endianness.Big,
            _ => throw context.Logger.Fail($"Invalid '{BitOrderProperty}' value '{property.Value}'. Expected 'lsb' or 'msb'.")
        };
    }

    public ProcessorKey Key => new("sys.bitpacker");
    public string Name => "Bit-Packer Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties =>
    [
        new(BitsProperty, typeof(int), false, description: "Field: the number of bits the field occupies."),
        new(BitOrderProperty, typeof(string), false, description: "Group: 'lsb' (default) or 'msb' bit order within a byte."),
    ];

    private sealed class BitPackerState
    {
        public BitWriter? Writer { get; set; }
        public BitReader? Reader { get; set; }
    }
}
