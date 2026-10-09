using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Maps a consecutive range of bits of a fixed-size integer field to a logical value (sys:bitfield).
/// Property 'bitoffset' is the position of the lowest bit of the range (bit 0 is the least significant bit, default 0);
/// 'bitlength' is the number of bits (default: all bits above the offset).
/// All the bytes of the field are consumed on read; on write the bits outside the range are zero.
/// The range must lie within the data type of the field.
/// </summary>
internal sealed class BitFieldProcessor : ProcessorBase, IFieldProcessor
{
    private static readonly PropertyDescriptor BitOffsetProperty = new("bitoffset", "sys.int32", false, description: "The position of the lowest bit of the range; bit 0 is the least significant bit (default 0).");
    private static readonly PropertyDescriptor BitLengthProperty = new("bitlength", "sys.int32", false, description: "The number of bits in the range (default: all bits above the offset).");

    public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
    {
        var (size, offset, length) = GetRange(context);
        var type = context.Field.DataType.ClrType;
        var signed = IsSigned(type);

        ulong bits;
        try
        {
            bits = signed
                ? (ulong)Convert.ToInt64(field.Value, System.Globalization.CultureInfo.InvariantCulture)
                : Convert.ToUInt64(field.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw context.Logger.Fail($"'{context.Field.Path}': cannot encode '{field.Value ?? "null"}' as {context.Field.DataType.Name}.", ex);
        }

        if (!Fits(bits, length, signed))
        {
            throw context.Logger.Fail($"'{context.Field.Path}': the value '{field.Value}' does not fit in {length} bits.");
        }

        var packed = (bits & Mask(length)) << offset;
        var bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(packed >> (8 * i));
        }

        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, size * 8), size * 8);
    }

    public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
    {
        var (size, offset, length) = GetRange(context);
        if (field.Value is not byte[] bytes)
        {
            throw context.Logger.Fail($"'{context.Field.Path}': the bit field processor expects bytes.");
        }
        if (bytes.Length < size)
        {
            return FieldReadResult<LogicalField>.NeedMoreData();
        }

        ulong value = 0;
        for (var i = size - 1; i >= 0; i--)
        {
            value = (value << 8) | bytes[i];
        }

        var type = context.Field.DataType.ClrType;
        var extracted = (value >> offset) & Mask(length);
        object result;
        if (IsSigned(type))
        {
            var signed = (long)extracted;
            if (length < 64 && (extracted & (1UL << (length - 1))) != 0)
            {
                signed |= (long)~Mask(length);
            }
            result = Convert.ChangeType(signed, type, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            result = Convert.ChangeType(extracted, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        return FieldReadResult<LogicalField>.Consumed(new(field.Name, type, result), size * 8);
    }

    private static (int Size, int Offset, int Length) GetRange(FieldProcessorContext context)
    {
        var path = context.Field.Path;
        var type = context.Field.DataType;
        if (type.FixedSize is not { } size || size > 8)
        {
            throw context.Logger.Fail($"'{path}': the bit field processor needs a fixed-size integer type of 1 to 8 bytes, not {type.Name}.");
        }

        var offset = context.Properties.GetOrDefault(BitOffsetProperty, 0);
        var length = context.Properties.GetOrDefault(BitLengthProperty, size * 8 - offset);
        if (offset < 0 || length < 1)
        {
            throw context.Logger.Fail($"'{path}': the bit offset ({offset}) cannot be negative and the bit length ({length}) must be at least 1.");
        }
        if (offset + length > size * 8)
        {
            throw context.Logger.Fail($"'{path}': the bit range (offset {offset} + length {length}) is larger than the {size * 8} bits of {type.Name}.");
        }
        return (size, offset, length);
    }

    private static ulong Mask(int bits) => bits >= 64 ? UInt64.MaxValue : (1UL << bits) - 1;

    private static bool Fits(ulong bits, int length, bool signed)
    {
        if (length >= 64)
        {
            return true;
        }
        if (!signed)
        {
            return (bits >> length) == 0;
        }
        var upper = (long)bits >> (length - 1);
        return upper is 0 or -1;
    }

    private static bool IsSigned(Type type)
        => type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long);

    public ProcessorKey Key => new("sys.bits");
    public string Name => "Bit-Field Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public IReadOnlyList<PropertyDescriptor> Properties => [BitOffsetProperty, BitLengthProperty];
}
