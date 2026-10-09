using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Joins the lowest 'bits' bits of each byte of one field into a single value (sys:bitslicer).
/// 'bytelength' is the number of bytes (required), 'bits' the bits taken from each byte (1 to 8, default 7)
/// and 'byteorder' selects the slice order: 'little' (least significant slice first, default) or 'big'.
/// The total number of bits (bits x bytelength) must fit the data type of the field.
/// The unused high bits of each byte are zero on write and must be zero on read.
/// </summary>
internal sealed class BitSlicerProcessor : ProcessorBase, IFieldProcessor
{
    private static readonly PropertyDescriptor ByteLengthProperty = new("bytelength", "sys.int32", false, description: "The number of bytes of the field. Not allowed when the field has a byte length.");
    private static readonly PropertyDescriptor BitsProperty = new("bits", "sys.int32", false, description: "The number of bits taken from each byte, 1 to 8 (default 7).");
    private static readonly PropertyDescriptor ByteOrderProperty = new("byteorder", "sys.endianness", false, description: "'little' (default, least significant slice first) or 'big'.");

    public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
    {
        var (byteLength, bits, order) = GetOptions(context);
        var path = context.Field.Path;

        ulong value;
        try
        {
            value = Convert.ToUInt64(field.Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw context.Logger.Fail($"'{path}': cannot encode '{field.Value ?? "null"}' as {byteLength} bytes of {bits} bits.", ex);
        }

        var totalBits = bits * byteLength;
        if (totalBits < 64 && (value >> totalBits) != 0)
        {
            throw context.Logger.Fail($"'{path}': the value '{value}' does not fit in {byteLength} bytes of {bits} bits.");
        }

        var bytes = BitSlicerCodec.Encode(value, bits, byteLength, order);
        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, bytes.Length * 8), bytes.Length * 8);
    }

    public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
    {
        var (byteLength, bits, order) = GetOptions(context);
        var path = context.Field.Path;

        if (field.Value is not byte[] bytes)
        {
            throw context.Logger.Fail($"'{path}': the bit slicer processor expects bytes.");
        }
        if (bytes.Length < byteLength)
        {
            return FieldReadResult<LogicalField>.NeedMoreData();
        }

        if (!BitSlicerCodec.TryDecode(bytes.AsSpan(0, byteLength), bits, order, out var value))
        {
            throw context.Logger.Fail($"'{path}': a byte has a bit set above the {bits} bits that are used.");
        }

        var type = context.Field.DataType.ClrType;
        return FieldReadResult<LogicalField>.Consumed(
            new(field.Name, type, Convert.ChangeType(value, type, System.Globalization.CultureInfo.InvariantCulture)),
            byteLength * 8);
    }

    private static (int ByteLength, int Bits, Endianness Order) GetOptions(FieldProcessorContext context)
    {
        var path = context.Field.Path;
        var type = context.Field.DataType;
        var hasProperty = context.Properties.TryGet<int>(ByteLengthProperty, out var propertyLength);
        if (hasProperty && context.FieldData.ByteLength is not null)
        {
            throw context.Logger.Fail($"'{path}': the field has a byte length; the '{context.Properties.FullName(ByteLengthProperty.Name)}' property cannot be used as well.");
        }
        var byteLength = hasProperty ? propertyLength : context.FieldData.ByteLength;
        if (byteLength is null)
        {
            throw context.Logger.Fail($"'{path}': the bit slicer processor requires the field byte length or the '{context.Properties.FullName(ByteLengthProperty.Name)}' property.");
        }
        var bits = context.Properties.GetOrDefault(BitsProperty, 7);
        var order = context.Properties.GetOrDefault(ByteOrderProperty, Endianness.Little);
        var length = byteLength.Value;

        if (!BitSlicerCodec.IsValid(bits, length))
        {
            throw context.Logger.Fail($"'{path}': {bits} bits of {length} bytes is not valid: 1 or more bytes, 1 to 8 bits per byte and at most 64 bits in total.");
        }
        if (type.FixedSize is not { } size || !IsInteger(type.ClrType))
        {
            throw context.Logger.Fail($"'{path}': the bit slicer processor needs a fixed-size integer type, not {type.Name}.");
        }

        var available = IsSigned(type.ClrType) ? size * 8 - 1 : size * 8;
        if (bits * length > available)
        {
            throw context.Logger.Fail($"'{path}': {length} bytes of {bits} bits ({bits * length} bits) is larger than the {available} bits of {type.Name}.");
        }

        return (length, bits, order);
    }

    private static bool IsSigned(Type type)
        => type == typeof(sbyte) || type == typeof(short) || type == typeof(int) || type == typeof(long);

    private static bool IsInteger(Type type)
        => IsSigned(type) || type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong);

    public ProcessorKey Key => new("sys.bitslicer");
    public string Name => "Bit Slicer Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public IReadOnlyList<PropertyDescriptor> Properties => [ByteLengthProperty, BitsProperty, ByteOrderProperty];
}
