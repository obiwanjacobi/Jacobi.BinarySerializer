using System.Buffers;
using System.Globalization;
using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Appends a CRC over all bytes of the group when writing and validates it when reading. No field represents the CRC.
/// Properties: 'algorithm' (a <see cref="CrcCodec"/> preset name, default 'crc32'), 'byteorder' ('big' (default) or 'little'),
/// and optional overrides 'width', 'poly', 'init', 'refin', 'refout', 'xorout' (numbers may be hex with 0x).
/// Usable as the layout head, or chained after a head (e.g. the byte packer).
/// </summary>
internal sealed class CrcProcessor : ILayoutProcessor,
    ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
{
    private static readonly PropertyDescriptor AlgorithmProperty = new("algorithm", "sys.string", false, description: "CRC preset name, default 'crc32'.");
    private static readonly PropertyDescriptor ByteOrderProperty = new("byteorder", "sys.endianness", false, description: "'big' (default) or 'little' byte order of the stored CRC.");
    private static readonly PropertyDescriptor WidthProperty = new("width", "sys.int32", false, description: "Override: CRC width in bits (1-64).");
    private static readonly PropertyDescriptor PolyProperty = new("poly", "sys.string", false, description: "Override: polynomial.");
    private static readonly PropertyDescriptor InitProperty = new("init", "sys.string", false, description: "Override: initial value.");
    private static readonly PropertyDescriptor RefInProperty = new("refin", "sys.boolean", false, description: "Override: reflect input bytes.");
    private static readonly PropertyDescriptor RefOutProperty = new("refout", "sys.boolean", false, description: "Override: reflect the output.");
    private static readonly PropertyDescriptor XorOutProperty = new("xorout", "sys.string", false, description: "Override: final XOR value.");

    private sealed class CrcState
    {
        public ulong Crc;
    }

    public ProcessorKey Key => new("sys.crc");
    public string Name => "CRC Processor";
    public PipelineStage Stage => PipelineStage.Layout;
    public IReadOnlyList<PropertyDescriptor> Properties =>
    [
        AlgorithmProperty, ByteOrderProperty, WidthProperty, PolyProperty, InitProperty, RefInProperty, RefOutProperty, XorOutProperty,
    ];

    public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
    {
        var state = context.GetOrCreateState<CrcState>();
        state.Crc = GetCodec(context).Initial;
    }

    // head
    public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
    {
        var result = ProcessorDefaults.DefaultLayoutProcessor.Write(writer, encodedValue, context);
        if (result == WriteResult.Success && encodedValue.Value is byte[] bytes)
        {
            Update(context, bytes);
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
        var codec = GetCodec(context);
        var crc = codec.Finish(context.GetOrCreateState<CrcState>().Crc);
        var length = codec.ByteLength;
        EndianCodec.Write(crc, writer.GetSpan(length).Slice(0, length), GetEndianness(context));
        writer.Advance(length);
    }

    public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
    }

    public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, context);

    LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        => LayoutReadResult<ReadOnlyMemory<byte>>.Success(LayoutChain.Unread(reader));

    public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
    {
        var codec = GetCodec(context);
        var length = context.GroupPosition;
        var data = reader.Sequence.Slice(reader.Consumed - length, length);
        var state = codec.Initial;
        foreach (var segment in data)
        {
            state = codec.Update(state, segment.Span);
        }
        var actual = codec.Finish(state);

        var size = codec.ByteLength;
        if (reader.Remaining < size)
        {
            throw context.Logger.Fail($"Not enough data to read the {codec.Width}-bit CRC.");
        }

        Span<byte> stored = stackalloc byte[size];
        reader.TryCopyTo(stored);
        reader.Advance(size);
        var expected = EndianCodec.Read(stored, GetEndianness(context));
        if (expected != actual)
        {
            throw context.Logger.Fail($"CRC mismatch: stored 0x{expected:X}, calculated 0x{actual:X}.");
        }
    }

    private static void Update(LayoutProcessorContext context, ReadOnlySpan<byte> bytes)
    {
        var state = context.GetOrCreateState<CrcState>();
        state.Crc = GetCodec(context).Update(state.Crc, bytes);
    }

    private static CrcCodec GetCodec(LayoutProcessorContext context)
    {
        var name = context.Properties.GetOrDefault(AlgorithmProperty, "crc32")!;
        if (!CrcCodec.TryGetPreset(name, out var p))
        {
            throw context.Logger.Fail($"Unknown CRC algorithm '{name}'.");
        }

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
