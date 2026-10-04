using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Converts values of the schema data types to and from their canonical fixed-width binary form.
/// Used by the default processors and by the session when a stage is empty.
/// </summary>
/// <remarks>
/// The canonical form is little-endian; endian conversion is a layout concern.
/// Conversions are range-checked: a value that does not fit the data type fails instead of wrapping.
/// </remarks>
public static class DataTypeCodec
{
    /// <summary>Size in bytes of the fixed-width form, or null when the type has no fixed width (String, None).</summary>
    public static int? FixedSize(SchemaDataType type)
        => type switch
        {
            SchemaDataType.Boolean or SchemaDataType.Int8 or SchemaDataType.UInt8 => 1,
            SchemaDataType.Int16 or SchemaDataType.UInt16 => 2,
            SchemaDataType.Int32 or SchemaDataType.UInt32 => 4,
            SchemaDataType.Int64 or SchemaDataType.UInt64 or SchemaDataType.Double or SchemaDataType.DateTime => 8,
            // TODO: String needs a length, terminator or prefix, to be provided by schema properties.
            _ => null
        };

    /// <summary>The CLR type that represents the data type, or null when not supported.</summary>
    public static Type? ClrType(SchemaDataType type)
        => type switch
        {
            SchemaDataType.String => typeof(string),
            SchemaDataType.Int8 => typeof(sbyte),
            SchemaDataType.Int16 => typeof(short),
            SchemaDataType.Int32 => typeof(int),
            SchemaDataType.Int64 => typeof(long),
            SchemaDataType.UInt8 => typeof(byte),
            SchemaDataType.UInt16 => typeof(ushort),
            SchemaDataType.UInt32 => typeof(uint),
            SchemaDataType.UInt64 => typeof(ulong),
            SchemaDataType.Boolean => typeof(bool),
            SchemaDataType.Double => typeof(double),
            SchemaDataType.DateTime => typeof(DateTime),
            _ => null
        };

    /// <summary>
    /// Encodes <paramref name="value"/> as the fixed-width, little-endian form of <paramref name="type"/>.
    /// Numeric values of another CLR type are converted when they fit. DateTime is stored as UTC ticks.
    /// Returns false for null, unsupported types and values that do not fit.
    /// </summary>
    public static bool TryEncode(SchemaDataType type, object? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || FixedSize(type) is not { } size)
        {
            return false;
        }

        ulong bits;
        try
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            bits = type switch
            {
                SchemaDataType.Boolean => Convert.ToBoolean(value, culture) ? 1UL : 0UL,
                SchemaDataType.Int8 => unchecked((ulong)Convert.ToSByte(value, culture)),
                SchemaDataType.UInt8 => Convert.ToByte(value, culture),
                SchemaDataType.Int16 => unchecked((ulong)Convert.ToInt16(value, culture)),
                SchemaDataType.UInt16 => Convert.ToUInt16(value, culture),
                SchemaDataType.Int32 => unchecked((ulong)Convert.ToInt32(value, culture)),
                SchemaDataType.UInt32 => Convert.ToUInt32(value, culture),
                SchemaDataType.Int64 => unchecked((ulong)Convert.ToInt64(value, culture)),
                SchemaDataType.UInt64 => Convert.ToUInt64(value, culture),
                SchemaDataType.Double => BitConverter.DoubleToUInt64Bits(Convert.ToDouble(value, culture)),
                SchemaDataType.DateTime => unchecked((ulong)Convert.ToDateTime(value, culture).Ticks),
                _ => throw new NotSupportedException()
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or NotSupportedException)
        {
            return false;
        }

        bytes = new byte[size];
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(bits >> (8 * i));
        }
        return true;
    }

    /// <summary>
    /// Decodes the fixed-width, little-endian form of <paramref name="type"/>.
    /// Returns false when the type has no fixed width or <paramref name="bytes"/> has the wrong length.
    /// </summary>
    public static bool TryDecode(SchemaDataType type, ReadOnlySpan<byte> bytes, out object? value)
    {
        value = null;
        if (FixedSize(type) is not { } size || bytes.Length != size)
        {
            return false;
        }

        ulong bits = 0;
        for (var i = 0; i < size; i++)
        {
            bits |= (ulong)bytes[i] << (8 * i);
        }

        switch (type)
        {
            case SchemaDataType.Boolean: value = bits != 0; return true;
            case SchemaDataType.Int8: value = unchecked((sbyte)bits); return true;
            case SchemaDataType.UInt8: value = unchecked((byte)bits); return true;
            case SchemaDataType.Int16: value = unchecked((short)bits); return true;
            case SchemaDataType.UInt16: value = unchecked((ushort)bits); return true;
            case SchemaDataType.Int32: value = unchecked((int)bits); return true;
            case SchemaDataType.UInt32: value = unchecked((uint)bits); return true;
            case SchemaDataType.Int64: value = unchecked((long)bits); return true;
            case SchemaDataType.UInt64: value = bits; return true;
            case SchemaDataType.Double: value = BitConverter.UInt64BitsToDouble(bits); return true;
            case SchemaDataType.DateTime:
                var ticks = unchecked((long)bits);
                if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                {
                    return false;
                }
                value = new DateTime(ticks, DateTimeKind.Utc);
                return true;
            default:
                return false;
        }
    }
}
