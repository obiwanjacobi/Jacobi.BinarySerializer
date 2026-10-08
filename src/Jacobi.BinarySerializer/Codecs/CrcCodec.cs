using System.Collections.Concurrent;

namespace Jacobi.BinarySerializer.Codecs;

/// <summary>
/// Parameters of a CRC following the Rocksoft model (as used by the reveng CRC catalog).
/// </summary>
public readonly record struct CrcParameters(int Width, ulong Poly, ulong Init, bool RefIn, bool RefOut, ulong XorOut)
{
    public static CrcParameters Crc32 { get; } = new(32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF);
    public static CrcParameters Crc32C { get; } = new(32, 0x1EDC6F41, 0xFFFFFFFF, true, true, 0xFFFFFFFF);
    public static CrcParameters Crc32Bzip2 { get; } = new(32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0xFFFFFFFF);
    public static CrcParameters Crc32Mpeg2 { get; } = new(32, 0x04C11DB7, 0xFFFFFFFF, false, false, 0);
    public static CrcParameters Crc16CcittFalse { get; } = new(16, 0x1021, 0xFFFF, false, false, 0);
    public static CrcParameters Crc16Xmodem { get; } = new(16, 0x1021, 0, false, false, 0);
    public static CrcParameters Crc16Kermit { get; } = new(16, 0x1021, 0, true, true, 0);
    public static CrcParameters Crc16X25 { get; } = new(16, 0x1021, 0xFFFF, true, true, 0xFFFF);
    public static CrcParameters Crc16Modbus { get; } = new(16, 0x8005, 0xFFFF, true, true, 0);
    public static CrcParameters Crc16Arc { get; } = new(16, 0x8005, 0, true, true, 0);
    public static CrcParameters Crc16Usb { get; } = new(16, 0x8005, 0xFFFF, true, true, 0xFFFF);
    public static CrcParameters Crc8 { get; } = new(8, 0x07, 0, false, false, 0);
    public static CrcParameters Crc8Maxim { get; } = new(8, 0x31, 0, true, true, 0);
    public static CrcParameters Crc8SaeJ1850 { get; } = new(8, 0x1D, 0xFF, false, false, 0xFF);
    public static CrcParameters Crc5Usb { get; } = new(5, 0x05, 0x1F, true, true, 0x1F);
    public static CrcParameters Crc24OpenPgp { get; } = new(24, 0x864CFB, 0xB704CE, false, false, 0);
    public static CrcParameters Crc64Xz { get; } = new(64, 0x42F0E1EBA9EA3693, ulong.MaxValue, true, true, ulong.MaxValue);
    public static CrcParameters Crc64Ecma182 { get; } = new(64, 0x42F0E1EBA9EA3693, 0, false, false, 0);
}

/// <summary>
/// Generic table-driven CRC for widths 1 to 64.
/// Incremental use: start with <see cref="Initial"/>, call <see cref="Update"/> for each part, finish with <see cref="Finish"/>.
/// </summary>
public sealed class CrcCodec
{
    private static readonly ConcurrentDictionary<CrcParameters, CrcCodec> Cache = new();

    private static readonly Dictionary<string, CrcParameters> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crc32"] = CrcParameters.Crc32,
        ["crc32c"] = CrcParameters.Crc32C,
        ["crc32-bzip2"] = CrcParameters.Crc32Bzip2,
        ["crc32-mpeg2"] = CrcParameters.Crc32Mpeg2,
        ["crc16-ccitt-false"] = CrcParameters.Crc16CcittFalse,
        ["crc16-xmodem"] = CrcParameters.Crc16Xmodem,
        ["crc16-kermit"] = CrcParameters.Crc16Kermit,
        ["crc16-x25"] = CrcParameters.Crc16X25,
        ["crc16-modbus"] = CrcParameters.Crc16Modbus,
        ["crc16-arc"] = CrcParameters.Crc16Arc,
        ["crc16-usb"] = CrcParameters.Crc16Usb,
        ["crc8"] = CrcParameters.Crc8,
        ["crc8-maxim"] = CrcParameters.Crc8Maxim,
        ["crc8-sae-j1850"] = CrcParameters.Crc8SaeJ1850,
        ["crc5-usb"] = CrcParameters.Crc5Usb,
        ["crc24-openpgp"] = CrcParameters.Crc24OpenPgp,
        ["crc64-xz"] = CrcParameters.Crc64Xz,
        ["crc64-ecma-182"] = CrcParameters.Crc64Ecma182,
    };

    private readonly ulong[]? _table;
    private readonly ulong _mask;

    private CrcCodec(CrcParameters parameters)
    {
        if (parameters.Width is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "CRC width must be 1 to 64 bits.");
        }

        Parameters = parameters;
        _mask = parameters.Width == 64 ? ulong.MaxValue : (1UL << parameters.Width) - 1;
        if (parameters.Width >= 8)
        {
            _table = BuildTable();
        }
    }

    public CrcParameters Parameters { get; }

    public int Width => Parameters.Width;

    public int ByteLength => (Parameters.Width + 7) / 8;

    public static IEnumerable<string> PresetNames => Presets.Keys;

    public static bool TryGetPreset(string name, out CrcParameters parameters)
        => Presets.TryGetValue(name, out parameters);

    public static CrcCodec Create(CrcParameters parameters)
        => Cache.GetOrAdd(parameters, static p => new CrcCodec(p));

    public static CrcCodec Create(string preset)
    {
        if (!TryGetPreset(preset, out var parameters))
        {
            throw new ArgumentException($"Unknown CRC algorithm '{preset}'.", nameof(preset));
        }
        return Create(parameters);
    }

    public ulong Initial => Parameters.Init & _mask;

    public ulong Update(ulong state, ReadOnlySpan<byte> data)
    {
        var width = Parameters.Width;
        foreach (var value in data)
        {
            var b = Parameters.RefIn ? Reflect(value, 8) : value;
            if (_table is not null)
            {
                var index = (byte)((state >> (width - 8)) ^ b);
                state = ((state << 8) ^ _table[index]) & _mask;
            }
            else
            {
                state = Bitwise(state, b);
            }
        }
        return state;
    }

    public ulong Finish(ulong state)
    {
        if (Parameters.RefOut)
        {
            state = Reflect(state, Parameters.Width);
        }
        return (state ^ Parameters.XorOut) & _mask;
    }

    public ulong Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    private ulong Bitwise(ulong state, ulong b)
    {
        var top = 1UL << (Parameters.Width - 1);
        for (var bit = 0x80; bit != 0; bit >>= 1)
        {
            var feedback = ((state & top) != 0) ^ ((b & (ulong)bit) != 0);
            state = (state << 1) & _mask;
            if (feedback)
            {
                state ^= Parameters.Poly;
            }
        }
        return state;
    }

    private ulong[] BuildTable()
    {
        var width = Parameters.Width;
        var top = 1UL << (width - 1);
        var table = new ulong[256];
        for (var i = 0; i < 256; i++)
        {
            var c = (ulong)i << (width - 8);
            for (var k = 0; k < 8; k++)
            {
                c = (c & top) != 0 ? (c << 1) ^ Parameters.Poly : c << 1;
            }
            table[i] = c & _mask;
        }
        return table;
    }

    private static ulong Reflect(ulong value, int width)
    {
        ulong result = 0;
        for (var i = 0; i < width; i++)
        {
            if ((value & (1UL << i)) != 0)
            {
                result |= 1UL << (width - 1 - i);
            }
        }
        return result;
    }
}
