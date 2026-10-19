using System.Buffers;
using System.Globalization;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Appends a CRC over all bytes of the group when writing and validates it when reading. No field represents the CRC.
/// Properties: 'algorithm' (a <see cref="CrcCodec"/> preset name, default 'crc32'), 'byteorder' ('big' (default) or 'little'),
/// and optional overrides 'width', 'poly', 'init', 'refin', 'refout', 'xorout' (numbers may be hex with 0x).
/// Usable as the innermost layout processor or as an outer byte wrapper (e.g. around the byte packer).
/// </summary>
internal sealed class CrcProcessor : ProcessorBase, ILayoutProcessor,
    ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
{
    public static readonly SchemaName AlgorithmDataType = "sys.crcalgorithm";

    /// <summary>The name under which the calculated CRC value (ulong) is published, in the namespace 'sys.crc' by default.</summary>
    public const string PublishedCrc = "value";

    public override IEnumerable<DataTypeDescriptor> DataTypes => [DataTypeDescriptor.ForEnum<CrcAlgorithm>(AlgorithmDataType)];

    private static readonly PropertyDescriptor AlgorithmProperty = new("algorithm", AlgorithmDataType, false, description: "CRC preset name, default 'crc32'.");
    private static readonly PropertyDescriptor ByteOrderProperty = new("byteorder", "sys.endianness", false, description: "'big' (default) or 'little' byte order of the stored CRC.");
    private static readonly PropertyDescriptor WidthProperty = new("width", "sys.int32", false, description: "Override: CRC width in bits (1-64).");
    private static readonly PropertyDescriptor PolyProperty = new("poly", "sys.string", false, description: "Override: polynomial.");
    private static readonly PropertyDescriptor InitProperty = new("init", "sys.string", false, description: "Override: initial value.");
    private static readonly PropertyDescriptor RefInProperty = new("refin", "sys.bool", false, description: "Override: reflect input bytes.");
    private static readonly PropertyDescriptor RefOutProperty = new("refout", "sys.bool", false, description: "Override: reflect the output.");
    private static readonly PropertyDescriptor XorOutProperty = new("xorout", "sys.string", false, description: "Override: final XOR value.");
    private static readonly PropertyDescriptor ValueProperty = new(PublishedCrc, "sys.uint64", false, isReadOnly: true, isPublished: true, description: "Published: the calculated CRC value.");

    private sealed class CrcState
    {
        public Stack<CrcScope> Scopes { get; } = new();
    }

    private sealed class CrcScope(CrcCodec codec)
    {
        public CrcCodec Codec { get; } = codec;
        public ulong Crc { get; set; } = codec.Initial;
    }

    public ProcessorKey Key => new("sys.crc");
    public string Name => "CRC Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties =>
    [
        AlgorithmProperty, ByteOrderProperty, WidthProperty, PolyProperty, InitProperty, RefInProperty, RefOutProperty, XorOutProperty, ValueProperty,
    ];

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        => context.GetOrCreateState<CrcState>().Scopes.Push(new CrcScope(GetCodec(context)));

    // head
    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        var result = DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);
        if (result == WriteResult.Success)
        {
            switch (encodedValue.Value)
            {
                case byte[] bytes:
                    Update(context, bytes);
                    break;
                case ReadOnlyMemory<byte> bytes:
                    Update(context, bytes.Span);
                    break;
                case Memory<byte> bytes:
                    Update(context, bytes.Span);
                    break;
            }
        }
        return result;
    }

    // chained
    public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
    {
        Update(context, bytes);
        writer.Write(bytes);
        return WriteResult.Success;
    }

    public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var scopes = context.GetOrCreateState<CrcState>().Scopes;
        var scope = scopes.Pop();
        var crc = scope.Codec.Finish(scope.Crc);
        context.Publish(PublishedCrc, crc);
        var length = scope.Codec.ByteLength;
        Span<byte> stored = stackalloc byte[length];
        EndianCodec.Write(crc, stored, GetEndianness(context));
        Update(context, stored);
        writer.Write(stored);
    }

    public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var codec = GetCodec(context);
        context.ReserveTail(codec.ByteLength);
        context.GetOrCreateState<CrcState>().Scopes.Push(new CrcScope(codec));
        return LayoutReadResult<EncodedField>.Success();
    }

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = BeginRead(ref reader, context);
        return new(result.Status, RequestedByteCount: result.RequestedByteCount);
    }

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var start = reader.Position;
        var consumed = reader.Consumed;
        var result = DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        if (result.Status == ReadResult.Success)
        {
            foreach (var segment in reader.Sequence.Slice(start, reader.Consumed - consumed))
            {
                Update(context, segment.Span);
            }
        }
        return result;
    }

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = LayoutChain.PassThroughRead(ref reader, context.InputByteLength);
        if (result.Status == ReadResult.Success)
        {
            Update(context, result.Value.Span);
        }
        return result;
    }

    public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var scopes = context.GetOrCreateState<CrcState>().Scopes;
        var scope = scopes.Peek();
        var size = scope.Codec.ByteLength;
        if (reader.Remaining < size)
        {
            return LayoutReadResult<EncodedField>.NeedMoreData(size);
        }

        var actual = scope.Codec.Finish(scope.Crc);
        Span<byte> stored = stackalloc byte[size];
        reader.TryCopyTo(stored);
        var expected = EndianCodec.Read(stored, GetEndianness(context));
        if (expected != actual)
        {
            throw context.Logger.Fail($"CRC mismatch: stored 0x{expected:X}, calculated 0x{actual:X}.");
        }
        reader.Advance(size);
        context.Publish(PublishedCrc, actual);
        scopes.Pop();
        Update(context, stored);
        return LayoutReadResult<EncodedField>.Success();
    }

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var result = EndRead(ref reader, context);
        return new(result.Status, RequestedByteCount: result.RequestedByteCount);
    }

    private static void Update(LayoutProcessorContext context, ReadOnlySpan<byte> bytes)
    {
        if (context.Field?.ByteOffset is not null)
        {
            return;
        }
        foreach (var scope in context.GetOrCreateState<CrcState>().Scopes)
        {
            scope.Crc = scope.Codec.Update(scope.Crc, bytes);
        }
    }

    private static CrcCodec GetCodec(LayoutProcessorContext context)
    {
        var p = context.Properties.GetOrDefault(AlgorithmProperty, CrcAlgorithm.Crc32).ToParameters();

        p = p with
        {
            Width = context.Properties.GetOrDefault(WidthProperty, p.Width),
            Poly = Number(context, PolyProperty, p.Poly),
            Init = Number(context, InitProperty, p.Init),
            XorOut = Number(context, XorOutProperty, p.XorOut),
            RefIn = context.Properties.GetOrDefault(RefInProperty, p.RefIn),
            RefOut = context.Properties.GetOrDefault(RefOutProperty, p.RefOut),
        };

        try
        {
            return CrcCodec.Create(p);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw context.Logger.Fail(ex.Message);
        }
    }

    private static ulong Number(LayoutProcessorContext context, PropertyDescriptor descriptor, ulong fallback)
    {
        var text = context.Properties.GetOrDefault<string>(descriptor);
        if (text is null)
        {
            return fallback;
        }

        var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return ok ? value : throw context.Logger.Fail($"Invalid '{descriptor.Name}' value '{text}'.");
    }

    private static Endianness GetEndianness(LayoutProcessorContext context)
        => context.Properties.GetOrDefault(ByteOrderProperty, Endianness.Big);
}
