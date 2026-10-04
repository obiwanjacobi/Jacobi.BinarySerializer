using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Variable-length integers (sys:varint). The 'encoding' property selects the algorithm:
/// 'leb128' (unsigned, default), 'sleb128' (signed LEB128) or 'zigzag' (protobuf sint: zigzag + LEB128).
/// </summary>
internal sealed class VarIntProcessor : IFieldProcessor
{
    private const string EncodingProperty = "encoding";

    public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
    {
        var encoding = GetEncoding(context);
        var type = context.Field.Field.Type;
        var path = context.Field.Path;

        byte[] bytes;
        if (encoding == VarIntEncoding.Leb128)
        {
            var value = ToUnsigned(field.Value, type, path);
            bytes = VarIntCodec.EncodeUnsigned(value);
        }
        else
        {
            var value = ToSigned(field.Value, type, path);
            bytes = encoding == VarIntEncoding.SLeb128 ? VarIntCodec.EncodeSigned(value) : VarIntCodec.EncodeZigZag(value);
        }

        return FieldWriteResult<EncodedField>.Written(new(field.Name, typeof(byte[]), bytes, bytes.Length * 8), bytes.Length * 8);
    }

    public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
    {
        var encoding = GetEncoding(context);
        var type = context.Field.Field.Type;
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
                ok = VarIntCodec.TryDecodeUnsigned(bytes, out var u, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromUnsigned(u, type, path, ref ok);
                break;
            case VarIntEncoding.SLeb128:
                ok = VarIntCodec.TryDecodeSigned(bytes, out var s, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromSigned(s, type, path, ref ok);
                break;
            default:
                ok = VarIntCodec.TryDecodeZigZag(bytes, out var z, out length);
                if (!ok) { return Incomplete(bytes, encoding, path); }
                result = FromSigned(z, type, path, ref ok);
                break;
        }

        if (!ok)
        {
            throw new InvalidOperationException($"'{path}': invalid {encoding} varint.");
        }

        return FieldReadResult<LogicalField>.Consumed(
            new(field.Name, DataTypeCodec.ClrType(type) ?? typeof(object), result), length * 8);
    }

    private static FieldReadResult<LogicalField> Incomplete(byte[] bytes, VarIntEncoding encoding, string path)
    {
        if (bytes.Length >= VarIntCodec.MaxLength)
        {
            throw new InvalidOperationException($"'{path}': invalid {encoding} varint.");
        }
        return FieldReadResult<LogicalField>.NeedMoreData();
    }

    private static VarIntEncoding GetEncoding(FieldProcessorContext context)
    {
        var text = context.Properties.GetOrDefault(EncodingProperty);
        return text?.ToLowerInvariant() switch
        {
            null or "leb128" => VarIntEncoding.Leb128,
            "sleb128" => VarIntEncoding.SLeb128,
            "zigzag" => VarIntEncoding.ZigZag,
            _ => throw new InvalidOperationException(
                $"Invalid '{context.Properties.FullName(EncodingProperty)}' value '{text}'. Expected 'leb128', 'sleb128' or 'zigzag'.")
        };
    }

    private static bool IsSignedType(SchemaDataType type)
        => type is SchemaDataType.Int8 or SchemaDataType.Int16 or SchemaDataType.Int32 or SchemaDataType.Int64;

    private static bool IsUnsignedType(SchemaDataType type)
        => type is SchemaDataType.UInt8 or SchemaDataType.UInt16 or SchemaDataType.UInt32 or SchemaDataType.UInt64;

    private static ulong ToUnsigned(object? value, SchemaDataType type, string path)
    {
        if (!IsUnsignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'leb128' encodes unsigned integers; use 'sleb128' or 'zigzag' for {type}.");
        }

        try
        {
            return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"'{path}': cannot encode '{value ?? "null"}' as {type}.", ex);
        }
    }

    private static long ToSigned(object? value, SchemaDataType type, string path)
    {
        if (!IsSignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'sleb128' and 'zigzag' encode signed integers; use 'leb128' for {type}.");
        }

        try
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"'{path}': cannot encode '{value ?? "null"}' as {type}.", ex);
        }
    }

    private static object FromUnsigned(ulong value, SchemaDataType type, string path, ref bool ok)
    {
        if (!IsUnsignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'leb128' decodes unsigned integers; use 'sleb128' or 'zigzag' for {type}.");
        }

        var max = type switch
        {
            SchemaDataType.UInt8 => (ulong)Byte.MaxValue,
            SchemaDataType.UInt16 => UInt16.MaxValue,
            SchemaDataType.UInt32 => UInt32.MaxValue,
            _ => UInt64.MaxValue
        };
        if (value > max)
        {
            ok = false;
        }

        return type switch
        {
            SchemaDataType.UInt8 => (byte)value,
            SchemaDataType.UInt16 => (ushort)value,
            SchemaDataType.UInt32 => (uint)value,
            _ => value
        };
    }

    private static object FromSigned(long value, SchemaDataType type, string path, ref bool ok)
    {
        if (!IsSignedType(type))
        {
            throw new InvalidOperationException($"'{path}': 'sleb128' and 'zigzag' decode signed integers; use 'leb128' for {type}.");
        }

        var (min, max) = type switch
        {
            SchemaDataType.Int8 => ((long)SByte.MinValue, (long)SByte.MaxValue),
            SchemaDataType.Int16 => (Int16.MinValue, Int16.MaxValue),
            SchemaDataType.Int32 => (Int32.MinValue, Int32.MaxValue),
            _ => (Int64.MinValue, Int64.MaxValue)
        };
        if (value < min || value > max)
        {
            ok = false;
        }

        return type switch
        {
            SchemaDataType.Int8 => (sbyte)value,
            SchemaDataType.Int16 => (short)value,
            SchemaDataType.Int32 => (int)value,
            _ => value
        };
    }

    public ProcessorKey Key => new("sys:varint");
    public string Name => "Variable Integer Processor";
    public PipelineStage Stage => PipelineStage.Representation;
    public IReadOnlyList<PropertyDescriptor> Properties =>
    [
        new(EncodingProperty, typeof(string), false, description: "'leb128' (unsigned, default), 'sleb128' (signed) or 'zigzag' (protobuf sint).")
    ];

    private enum VarIntEncoding
    {
        Leb128,
        SLeb128,
        ZigZag
    }
}
