using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Descriptors;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Variable-length integers (sys:varint). The 'encoding' property selects the algorithm:
/// 'leb128' (unsigned, default), 'sleb128' (signed LEB128), 'zigzag' (protobuf sint: zigzag + LEB128),
/// 'vlq' (MIDI big-endian base-128, unsigned) or 'prefix' (UTF-8 style length prefix, unsigned).
/// </summary>
internal sealed class VarIntProcessor : IFieldProcessor
{
    private static readonly PropertyDescriptor EncodingProperty = new("encoding", "sys.string", false, description: "'leb128' (unsigned, default), 'sleb128' (signed), 'zigzag' (protobuf sint), 'vlq' (MIDI, unsigned) or 'prefix' (length-prefixed, unsigned).");

    public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
    {
        try
        {
            return WriteCore(field, context);
        }
        catch (InvalidOperationException ex)
        {
            throw context.Logger.Fail(ex.Message, ex);
        }
    }

    public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
    {
        try
        {
            return ReadCore(field, context);
        }
        catch (InvalidOperationException ex)
        {
            throw context.Logger.Fail(ex.Message, ex);
        }
    }

    private static FieldWriteResult<EncodedField> WriteCore(LogicalField field, FieldProcessorContext context)
    {
        var encoding = GetEncoding(context);
        var type = context.Field.DataType;
        var path = context.Field.Path;

        byte[] bytes;
        if (encoding is VarIntEncoding.Leb128 or VarIntEncoding.Vlq or VarIntEncoding.Prefix)
        {
            var value = ToUnsigned(field.Value, type, path);
            bytes = encoding switch
            {
                VarIntEncoding.Vlq => VlqCodec.Encode(value),
                VarIntEncoding.Prefix => PrefixVarIntCodec.Encode(value),
                _ => Leb128Codec.Encode(value)
            };
        }
        else
        {
            var value = ToSigned(field.Value, type, path);
            bytes = encoding == VarIntEncoding.SLeb128 ? Sleb128Codec.Encode(value) : ZigZagCodec.Encode(value);
        }

        context.Logger.VarIntEncoded(path, encoding.ToString(), bytes.Length);
        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, bytes.Length * 8), bytes.Length * 8);
    }

    private static FieldReadResult<LogicalField> ReadCore(EncodedField field, FieldProcessorContext context)
    {
        var encoding = GetEncoding(context);
        var type = context.Field.DataType;
        var path = context.Field.Path;

        if (field.Value is not byte[] bytes)
        {
            throw new InvalidOperationException($"'{path}': the varint processor expects bytes.");
        }

        object result;
        bool ok;
        int length;
        switch (encoding)
        {
            case VarIntEncoding.Leb128:
                ok = Leb128Codec.TryDecode(bytes, out var u, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromUnsigned(u, type, path, ref ok);
                break;
            case VarIntEncoding.Vlq:
                ok = VlqCodec.TryDecode(bytes, out var v, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromUnsigned(v, type, path, ref ok);
                break;
            case VarIntEncoding.Prefix:
                ok = PrefixVarIntCodec.TryDecode(bytes, out var p, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromUnsigned(p, type, path, ref ok);
                break;
            case VarIntEncoding.SLeb128:
                ok = Sleb128Codec.TryDecode(bytes, out var s, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromSigned(s, type, path, ref ok);
                break;
            default:
                ok = ZigZagCodec.TryDecode(bytes, out var z, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromSigned(z, type, path, ref ok);
                break;
        }

        if (!ok)
        {
            throw new InvalidOperationException($"'{path}': invalid {encoding} varint.");
        }

        context.Logger.VarIntDecoded(path, encoding.ToString(), bytes.Length);
        return FieldReadResult<LogicalField>.Consumed(
            new(field.Name, type.ClrType, result), length * 8);
    }

    private static FieldReadResult<LogicalField> Incomplete(byte[] bytes, VarIntEncoding encoding, string path)
    {
        var max = encoding == VarIntEncoding.Prefix ? PrefixVarIntCodec.MaxLength : Leb128Codec.MaxLength;
        if (bytes.Length >= max)
        {
            throw new InvalidOperationException($"'{path}': invalid {encoding} varint.");
        }
        return FieldReadResult<LogicalField>.NeedMoreData();
    }

    private static VarIntEncoding GetEncoding(FieldProcessorContext context)
    {
        var text = context.Properties.GetOrDefault<string>(EncodingProperty);
        return text?.ToLowerInvariant() switch
        {
            null or "leb128" => VarIntEncoding.Leb128,
            "sleb128" => VarIntEncoding.SLeb128,
            "zigzag" => VarIntEncoding.ZigZag,
            "vlq" => VarIntEncoding.Vlq,
            "prefix" => VarIntEncoding.Prefix,
            _ => throw new InvalidOperationException(
                $"Invalid '{context.Properties.FullName(EncodingProperty.Name)}' value '{text}'. Expected 'leb128', 'sleb128', 'zigzag', 'vlq' or 'prefix'.")
        };
    }

    private static bool IsSignedType(DataTypeDescriptor type)
        => type.ClrType == typeof(sbyte) || type.ClrType == typeof(short) || type.ClrType == typeof(int) || type.ClrType == typeof(long);

    private static bool IsUnsignedType(DataTypeDescriptor type)
        => type.ClrType == typeof(byte) || type.ClrType == typeof(ushort) || type.ClrType == typeof(uint) || type.ClrType == typeof(ulong);

    private static ulong ToUnsigned(object? value, DataTypeDescriptor type, string path)
    {
        if (!IsUnsignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'leb128' encodes unsigned integers; use 'sleb128' or 'zigzag' for {type.Name}.");
        }

        try
        {
            return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"'{path}': cannot encode '{value ?? "null"}' as {type.Name}.", ex);
        }
    }

    private static long ToSigned(object? value, DataTypeDescriptor type, string path)
    {
        if (!IsSignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'sleb128' and 'zigzag' encode signed integers; use 'leb128' for {type.Name}.");
        }

        try
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"'{path}': cannot encode '{value ?? "null"}' as {type.Name}.", ex);
        }
    }

    private static object FromUnsigned(ulong value, DataTypeDescriptor type, string path, ref bool ok)
    {
        if (!IsUnsignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'leb128' decodes unsigned integers; use 'sleb128' or 'zigzag' for {type.Name}.");
        }

        var max = type.FixedSize is { } size and < 8 ? (1UL << (size * 8)) - 1 : UInt64.MaxValue;
        if (value > max)
        {
            ok = false;
        }

        var clr = type.ClrType;
        return clr == typeof(byte) ? (byte)value
            : clr == typeof(ushort) ? (ushort)value
            : clr == typeof(uint) ? (uint)value
            : value;
    }

    private static object FromSigned(long value, DataTypeDescriptor type, string path, ref bool ok)
    {
        if (!IsSignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'sleb128' and 'zigzag' decode signed integers; use 'leb128' for {type.Name}.");
        }

        var bits = type.FixedSize is { } size and < 8 ? size * 8 : 64;
        var max = bits == 64 ? Int64.MaxValue : (1L << (bits - 1)) - 1;
        var min = bits == 64 ? Int64.MinValue : -(1L << (bits - 1));
        if (value < min || value > max)
        {
            ok = false;
        }

        var clr = type.ClrType;
        return clr == typeof(sbyte) ? (sbyte)value
            : clr == typeof(short) ? (short)value
            : clr == typeof(int) ? (int)value
            : value;
    }

    public ProcessorKey Key => new("sys.varint");
    public string Name => "Variable Integer Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public IReadOnlyList<PropertyDescriptor> Properties => [EncodingProperty];

    private enum VarIntEncoding
    {
        Leb128,
        SLeb128,
        ZigZag,
        Vlq,
        Prefix
    }
}
