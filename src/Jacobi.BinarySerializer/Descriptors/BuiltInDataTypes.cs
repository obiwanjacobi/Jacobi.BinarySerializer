using System.Globalization;
using System.Numerics;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Descriptors;

/// <summary>
/// The default set of data type descriptors that a <see cref="DataTypeRegistry"/> is prepopulated with.
/// </summary>
/// <remarks>
/// The default representation is the fixed-width, little-endian form; endian conversion is a layout concern.
/// Conversions are range-checked: a value that does not fit the data type fails instead of wrapping.
/// </remarks>
internal static class BuiltInDataTypes
{
    public static IEnumerable<DataTypeDescriptor> Create()
    {
        yield return CreateString();
        yield return Integer<sbyte>("int8", 1, v => unchecked((ulong)Convert.ToSByte(v, Culture)), b => unchecked((sbyte)b));
        yield return Integer<short>("int16", 2, v => unchecked((ulong)Convert.ToInt16(v, Culture)), b => unchecked((short)b));
        yield return Integer<int>("int32", 4, v => unchecked((ulong)Convert.ToInt32(v, Culture)), b => unchecked((int)b));
        yield return Integer<long>("int64", 8, v => unchecked((ulong)Convert.ToInt64(v, Culture)), b => unchecked((long)b));
        yield return Integer<byte>("uint8", 1, v => Convert.ToByte(v, Culture), b => unchecked((byte)b));
        yield return Integer<ushort>("uint16", 2, v => Convert.ToUInt16(v, Culture), b => unchecked((ushort)b));
        yield return Integer<uint>("uint32", 4, v => Convert.ToUInt32(v, Culture), b => unchecked((uint)b));
        yield return Integer<ulong>("uint64", 8, v => Convert.ToUInt64(v, Culture), b => b);
        yield return CreateBoolean();
        yield return CreateDouble();
        yield return new DataTypeDescriptor(Name("decimal"), typeof(decimal),
            (string? text, out object? value) =>
            {
                value = null;
                if (text is not null && Decimal.TryParse(text, NumberStyles.Number, Culture, out var number)) { value = number; return true; }
                return false;
            });
        yield return CreateDateTime();
        yield return CreateBytes();
        yield return DataTypeDescriptor.ForEnum<Codecs.Endianness>(Name("endianness"));
        yield return new DataTypeDescriptor(Name("object"), typeof(object), (string? text, out object? value) => { value = text; return text is not null; });
    }

    private static CultureInfo Culture => CultureInfo.InvariantCulture;

    private static SchemaName Name(string name)
        => new($"{DataTypeRegistry.SystemNamespace}{SchemaName.Separator}{name}");

    private static DataTypeDescriptor CreateString()
        => new(Name("string"), typeof(string), (string? text, out object? value) => { value = text; return text is not null; });

    private static DataTypeDescriptor CreateBoolean()
        => Fixed<bool>("boolean", 1,
            (string? text, out object? value) =>
            {
                value = null;
                if (text is not null && Boolean.TryParse(text, out var flag)) { value = flag; return true; }
                return false;
            },
            v => Convert.ToBoolean(v, Culture) ? 1UL : 0UL,
            bits => bits != 0);

    private static DataTypeDescriptor CreateDouble()
        => Fixed<double>("double", 8,
            (string? text, out object? value) =>
            {
                value = null;
                if (text is not null && Double.TryParse(text, NumberStyles.Float, Culture, out var number)) { value = number; return true; }
                return false;
            },
            v => BitConverter.DoubleToUInt64Bits(Convert.ToDouble(v, Culture)),
            bits => BitConverter.UInt64BitsToDouble(bits));

    private static DataTypeDescriptor CreateDateTime()
        => Fixed<DateTime>("datetime", 8,
            (string? text, out object? value) =>
            {
                value = null;
                if (text is not null && DateTime.TryParse(text, Culture, DateTimeStyles.RoundtripKind, out var date)) { value = date; return true; }
                return false;
            },
            v => unchecked((ulong)Convert.ToDateTime(v, Culture).Ticks),
            bits =>
            {
                var ticks = unchecked((long)bits);
                return ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                    ? null
                    : new DateTime(ticks, DateTimeKind.Utc);
            });

    private static DataTypeDescriptor Integer<T>(string name, int size, Func<object, ulong> toBits, Func<ulong, object?> fromBits)
        where T : IBinaryInteger<T>
        => Fixed<T>(name, size,
            (string? text, out object? value) =>
            {
                value = null;
                if (text is null) return false;

                var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                var styles = hex ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
                if (T.TryParse(hex ? text[2..] : text, styles, Culture, out var number)) { value = number; return true; }
                return false;
            },
            toBits, fromBits);

    private static DataTypeDescriptor Fixed<T>(string name, int size, DataTypeParser parse, Func<object, ulong> toBits, Func<ulong, object?> fromBits)
        => new(Name(name), typeof(T), parse)
        {
            FixedSize = size,
            Encode = (object? value, out byte[] bytes) =>
            {
                bytes = [];
                if (value is null) return false;

                ulong bits;
                try
                {
                    bits = toBits(value);
                }
                catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
                {
                    return false;
                }

                bytes = new byte[size];
                for (var i = 0; i < size; i++)
                {
                    bytes[i] = (byte)(bits >> (8 * i));
                }
                return true;
            },
            Decode = (byte[] bytes, out object? value) =>
            {
                value = null;
                if (bytes.Length != size) return false;

                ulong bits = 0;
                for (var i = 0; i < size; i++)
                {
                    bits |= (ulong)bytes[i] << (8 * i);
                }
                value = fromBits(bits);
                return value is not null;
            },
        };

    private static DataTypeDescriptor CreateBytes()
        => new(Name("bytes"), typeof(byte[]),
            (string? text, out object? value) =>
            {
                value = null;
                if (text is null) return false;

                var hexText = new string(text.Where(c => !Char.IsWhiteSpace(c)).ToArray());
                if (hexText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    hexText = hexText[2..];
                }
                if (hexText.Length % 2 == 0 && hexText.All(Char.IsAsciiHexDigit))
                {
                    value = Convert.FromHexString(hexText);
                    return true;
                }
                return false;
            })
        {
            SupportsLength = true,
            TakesRestOfWindow = true,
            Encode = (object? value, out byte[] bytes) =>
            {
                bytes = [];
                switch (value)
                {
                    case byte[] array: bytes = array; return true;
                    case ReadOnlyMemory<byte> memory: bytes = memory.ToArray(); return true;
                    case Memory<byte> memory: bytes = memory.ToArray(); return true;
                    default: return false;
                }
            },
            Decode = (byte[] bytes, out object? value) =>
            {
                value = bytes.ToArray();
                return true;
            },
        };
}
