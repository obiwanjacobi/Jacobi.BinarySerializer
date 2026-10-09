using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Maps a consecutive range of bits of an integer value to a logical value (sys:bits).
/// Property 'bitoffset' is the position of the lowest bit of the range (bit 0 is the least significant bit, default 0);
/// 'bitlength' is the number of bits (default: all bits above the offset).
/// On write, the bits outside the range are zero.
/// The range must lie within the data type of the value (a field, or the value of a repeat count or choice index).
/// </summary>
internal sealed class BitsProcessor : ProcessorBase, IValueProcessor
{
    private static readonly PropertyDescriptor BitOffsetProperty = new("bitoffset", "sys.int32", false, description: "The position of the lowest bit of the range; bit 0 is the least significant bit (default 0).");
    private static readonly PropertyDescriptor BitLengthProperty = new("bitlength", "sys.int32", false, description: "The number of bits in the range (default: all bits above the offset).");

    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var (type, offset, length) = GetRange(context);
        var signed = IsSigned(type);

        ulong bits;
        try
        {
            bits = signed
                ? (ulong)Convert.ToInt64(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture)
                : Convert.ToUInt64(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw context.Logger.Fail($"'{PathOf(context)}': cannot use '{logicalValue.Value}' as a bit range value.", ex);
        }

        if (!Fits(bits, length, signed))
        {
            throw context.Logger.Fail($"'{PathOf(context)}': the value '{logicalValue.Value}' does not fit in {length} bits.");
        }

        var packed = (bits & Mask(length)) << offset;
        var clrType = type.ClrType;
        var result = signed
            ? Convert.ChangeType(unchecked((long)SignExtend(packed, type.FixedSize!.Value * 8)), clrType, System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ChangeType(packed, clrType, System.Globalization.CultureInfo.InvariantCulture);
        return new(logicalValue.Name, clrType, result);
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        if (logicalValue.Value is null)
        {
            return logicalValue;
        }

        var (type, offset, length) = GetRange(context);
        var signed = IsSigned(type);

        ulong value;
        try
        {
            value = signed
                ? unchecked((ulong)Convert.ToInt64(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture))
                : Convert.ToUInt64(logicalValue.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw context.Logger.Fail($"'{PathOf(context)}': cannot use '{logicalValue.Value}' as a bit range value.", ex);
        }

        var extracted = (value >> offset) & Mask(length);
        var clrType = type.ClrType;
        object result = signed
            ? Convert.ChangeType(unchecked((long)SignExtend(extracted, length)), clrType, System.Globalization.CultureInfo.InvariantCulture)
            : Convert.ChangeType(extracted, clrType, System.Globalization.CultureInfo.InvariantCulture);
        return new(logicalValue.Name, clrType, result);
    }

    private static string PathOf(ValueProcessorContext context)
        => context.Field?.Path.ToString() ?? context.Group?.Path.ToString() ?? String.Empty;

    private static (Descriptors.DataTypeDescriptor Type, int Offset, int Length) GetRange(ValueProcessorContext context)
    {
        var path = PathOf(context);
        var type = context.DataType;
        if (type?.FixedSize is not { } size || size > 8)
        {
            throw context.Logger.Fail($"'{path}': the bits processor needs a fixed-size integer type of 1 to 8 bytes, not {type?.Name ?? "unknown"}.");
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
        return (type, offset, length);
    }

    private static ulong Mask(int bits) => bits >= 64 ? UInt64.MaxValue : (1UL << bits) - 1;

    private static ulong SignExtend(ulong value, int bits)
        => bits < 64 && (value & (1UL << (bits - 1))) != 0 ? value | ~Mask(bits) : value;

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

    private static bool IsSigned(Descriptors.DataTypeDescriptor type)
        => type.ClrType == typeof(sbyte) || type.ClrType == typeof(short) || type.ClrType == typeof(int) || type.ClrType == typeof(long);

    public ProcessorKey Key => new("sys.bits");
    public string Name => "Bits Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [BitOffsetProperty, BitLengthProperty];
}
