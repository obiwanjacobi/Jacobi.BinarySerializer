using System.Buffers;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Packs canonical integer and boolean field representations into a group's continuous bit stream.
/// Field properties 'bits' (default: 1) and absolute 'bitoffset' select the occupied bits.
/// Processor properties 'bitorder' (default: little) and optional 'byteLength' select order and capacity.
/// </summary>
internal sealed class BitPackerProcessor : ProcessorBase, ILayoutProcessor
{
    private static readonly PropertyDescriptor BitsProperty = new("bits", "sys.int32", false, description: "Field: the number of bits the field occupies (default: 1).");
    private static readonly PropertyDescriptor BitOffsetProperty = new("bitoffset", "sys.int32", false, description: "Field: absolute bit offset from the owning group, in packing order.");
    private static readonly PropertyDescriptor BitOrderProperty = new("bitorder", "sys.endianness", false, description: "Group: 'little' (default) or 'big' bit order within a byte.");
    private static readonly PropertyDescriptor ByteLengthProperty = new("byteLength", "sys.int32", false, description: "Optional capacity in bytes; otherwise the bit stream is rounded up to whole bytes.");

    private sealed class State
    {
        public Stack<Scope> Scopes { get; } = new();
    }

    private sealed class Scope
    {
        public Scope(Endianness order, int? capacity)
        {
            Reader = new BitReader(order);
            Writer = new BitWriter(Buffer, order);
            Capacity = capacity;
        }

        public ArrayBufferWriter<byte> Buffer { get; } = new();
        public BitReader Reader { get; }
        public BitWriter Writer { get; }
        public int? Capacity { get; }
        public long Position { get; set; }
    }

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var scopes = context.GetOrCreateState<State>().Scopes;
        if (scopes.TryPeek(out var parent))
        {
            parent.Writer.Flush();
            Drain(writer, parent);
            parent.Position = (parent.Position + 7) / 8 * 8;
        }
        Begin(context);
        context.ByteBlockSize = 1;
    }

    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        var scope = context.GetOrCreateState<State>().Scopes.Peek();
        if (!TryGetField(context, scope, out var field, out var size, out var bits, out var offset))
        {
            return WriteResult.Failure;
        }

        if (encodedValue.Value is not byte[] bytes || encodedValue.PhysicalType != typeof(byte[])
            || bytes.Length != size || encodedValue.BitWidth != size * 8)
        {
            Reject(context, "only canonical fixed-width byte representations are supported");
            return WriteResult.Failure;
        }

        ulong value = 0;
        for (var b = bytes.Length - 1; b >= 0; b--)
        {
            value = (value << 8) | bytes[b];
        }

        if (!Fits(value, size * 8, bits, IsSigned(field)))
        {
            Reject(context, $"the value does not fit in {bits} bits");
            return WriteResult.Failure;
        }

        WriteZeros(scope.Writer, offset - scope.Position);
        scope.Writer.Write(value, bits);
        scope.Position = offset + bits;
        Drain(writer, scope);
        context.Logger.BitsPacked(field.Path, bits);
        return WriteResult.Success;
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var scopes = context.GetOrCreateState<State>().Scopes;
        var scope = scopes.Peek();
        if (scope.Capacity is { } capacity)
        {
            WriteZeros(scope.Writer, capacity * 8L - scope.Position);
        }
        scope.Writer.Flush();
        Drain(writer, scope);
        EndScope(scopes, context);
    }

    public LayoutReadResult BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var scopes = context.GetOrCreateState<State>().Scopes;
        if (scopes.TryPeek(out var parent))
        {
            parent.Reader.Align();
            parent.Position = (parent.Position + 7) / 8 * 8;
        }
        Begin(context);
        context.ByteBlockSize = 1;
        context.HasBufferedInput = false;
        return LayoutReadResult.Success();
    }

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var scope = context.GetOrCreateState<State>().Scopes.Peek();
        if (!TryGetField(context, scope, out var field, out var size, out var bits, out var offset))
        {
            return LayoutReadResult<EncodedField>.Failure();
        }

        var gap = offset - scope.Position;
        var missing = gap + bits - scope.Reader.PendingBits;
        var required = missing > 0 ? (int)((missing + 7) / 8) : 0;
        if (reader.Remaining < required)
        {
            return LayoutReadResult<EncodedField>.NeedMoreData(required);
        }

        while (gap > 0)
        {
            var take = (int)Math.Min(gap, 64);
            scope.Reader.TryRead(ref reader, take, out _);
            gap -= take;
        }
        scope.Reader.TryRead(ref reader, bits, out var value);
        if (IsSigned(field) && bits < 64 && (value & (1UL << (bits - 1))) != 0)
        {
            value |= ~BitWriter.Mask(bits);
        }

        var encoded = new byte[size];
        for (var b = 0; b < size; b++)
        {
            encoded[b] = (byte)(value >> (8 * b));
        }
        scope.Position = offset + bits;
        context.HasBufferedInput = scope.Reader.PendingBits > 0;
        context.Logger.BitsUnpacked(field.Path, bits);
        return LayoutReadResult<EncodedField>.Success(new(field.Name, typeof(byte[]), encoded, size * 8));
    }

    public LayoutReadResult EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var scopes = context.GetOrCreateState<State>().Scopes;
        var scope = scopes.Peek();
        var padding = scope.Capacity is { } capacity ? capacity - (scope.Position + 7) / 8 : 0;
        if (reader.Remaining < padding)
        {
            return LayoutReadResult.NeedMoreData((int)padding);
        }
        reader.Advance(padding);
        EndScope(scopes, context);
        return LayoutReadResult.Success();
    }

    private static void EndScope(Stack<Scope> scopes, LayoutProcessorContext context)
    {
        var completed = scopes.Pop();
        if (scopes.TryPeek(out var parent))
        {
            var bytes = completed.Capacity ?? (completed.Position + 7) / 8;
            var position = parent.Position + bytes * 8;
            if (parent.Capacity is { } capacity && position > capacity * 8L)
            {
                throw context.Logger.Fail("The nested bit-packer scope exceeds its parent's byte capacity.");
            }
            parent.Position = position;
        }
        context.HasBufferedInput = parent?.Reader.PendingBits > 0;
    }

    private static void Begin(LayoutProcessorContext context)
    {
        ValidateVirtualFields(context.Group, context);
        int? capacity = context.Properties.TryGet<int>(ByteLengthProperty, out var length) ? length : null;
        if (capacity < 0)
        {
            throw context.Logger.Fail("The bit-packer's byteLength cannot be negative.");
        }
        var order = context.Properties.GetOrDefault(BitOrderProperty,
            context.PropertiesOf(context.Group.Group.Properties).GetOrDefault(BitOrderProperty, Endianness.Little));
        context.GetOrCreateState<State>().Scopes.Push(new Scope(order, capacity));
    }

    private static void ValidateVirtualFields(GroupInfo group, LayoutProcessorContext context)
    {
        foreach (var member in group.Members)
        {
            if (member is FieldInfo { ByteOffset: not null } field
                && field.Pipeline.LayoutProcessors.LastOrDefault()?.Processor.Key == new ProcessorKey("sys.bitpacker"))
            {
                throw context.Logger.Fail($"'{field.Path}': virtual fields with byteOffset are not supported by the bit packer.");
            }
            if (member is GroupInfo nested)
            {
                ValidateVirtualFields(nested, context);
            }
        }
    }

    private static bool TryGetField(LayoutProcessorContext context, Scope scope,
        out FieldInfo field, out int size, out int bits, out long offset)
    {
        field = context.Field!;
        size = field?.DataType.FixedSize ?? 0;
        bits = 0;
        offset = scope.Position;
        if (field is null || size is < 1 or > 8 || !IsIntegerOrBoolean(field))
        {
            return Reject(context, "only fixed-width integer and boolean data types of 1 to 8 bytes are supported");
        }
        if (field.ByteOffset is not null)
        {
            return Reject(context, "virtual fields with byteOffset are not supported by the bit packer");
        }
        if (field.Pipeline.FieldProcessors.Count > 0 || context.FieldData.ByteLength is { } length && length != size)
        {
            return Reject(context, "only the complete default fixed-width field representation is supported");
        }

        var properties = context.PropertiesOf(field.Field.Properties);
        bits = properties.GetOrDefault(BitsProperty, 1);
        if (bits < 1 || bits > size * 8)
        {
            return Reject(context, $"{bits} bits is out of range");
        }
        if (properties.TryGet<int>(BitOffsetProperty, out var bitOffset))
        {
            offset = bitOffset;
        }
        if (offset < scope.Position)
        {
            return Reject(context, "bitoffset cannot move backwards or overlap an earlier field");
        }
        if (scope.Capacity is { } capacity && offset + bits > capacity * 8L)
        {
            return Reject(context, $"the field does not fit in {capacity} byte(s)");
        }
        return true;
    }

    private static bool Reject(LayoutProcessorContext context, string reason)
    {
        context.Logger.BitsRejected(context.Field?.Path.ToString() ?? context.Group.Path.ToString(), reason);
        return false;
    }

    private static void Drain(IBufferWriter<byte> writer, Scope scope)
    {
        writer.Write(scope.Buffer.WrittenSpan);
        scope.Buffer.Clear();
    }

    private static void WriteZeros(BitWriter writer, long bits)
    {
        while (bits > 0)
        {
            var take = (int)Math.Min(bits, 64);
            writer.Write(0, take);
            bits -= take;
        }
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
        var upperMask = BitWriter.Mask(storedBits - bits);
        var signBit = (value >> (bits - 1)) & 1;
        return upper == (signBit == 1 ? upperMask : 0);
    }

    private static bool IsSigned(FieldInfo field)
        => field.DataType.ClrType == typeof(sbyte) || field.DataType.ClrType == typeof(short)
            || field.DataType.ClrType == typeof(int) || field.DataType.ClrType == typeof(long);

    private static bool IsIntegerOrBoolean(FieldInfo field)
        => IsSigned(field) || field.DataType.ClrType == typeof(byte) || field.DataType.ClrType == typeof(ushort)
            || field.DataType.ClrType == typeof(uint) || field.DataType.ClrType == typeof(ulong)
            || field.DataType.ClrType == typeof(bool);

    public ProcessorKey Key => new("sys.bitpacker");
    public string Name => "Bit-Packer Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties => [BitsProperty, BitOffsetProperty, BitOrderProperty, ByteLengthProperty];
}
