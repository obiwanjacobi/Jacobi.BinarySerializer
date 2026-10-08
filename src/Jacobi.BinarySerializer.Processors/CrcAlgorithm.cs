using Jacobi.BinarySerializer.Codecs;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// The CRC presets of the crc processor ('algorithm' property).
/// Schema literals ignore case, '-' and '_' (e.g. 'crc32-bzip2').
/// </summary>
internal enum CrcAlgorithm
{
    Crc32,
    Crc32C,
    Crc32Bzip2,
    Crc32Mpeg2,
    Crc16CcittFalse,
    Crc16Xmodem,
    Crc16Kermit,
    Crc16X25,
    Crc16Modbus,
    Crc16Arc,
    Crc16Usb,
    Crc8,
    Crc8Maxim,
    Crc8SaeJ1850,
    Crc5Usb,
    Crc24OpenPgp,
    Crc64Xz,
    Crc64Ecma182,
}

internal static class CrcAlgorithmExtensions
{
    public static CrcParameters ToParameters(this CrcAlgorithm algorithm)
        => algorithm switch
        {
            CrcAlgorithm.Crc32 => CrcParameters.Crc32,
            CrcAlgorithm.Crc32C => CrcParameters.Crc32C,
            CrcAlgorithm.Crc32Bzip2 => CrcParameters.Crc32Bzip2,
            CrcAlgorithm.Crc32Mpeg2 => CrcParameters.Crc32Mpeg2,
            CrcAlgorithm.Crc16CcittFalse => CrcParameters.Crc16CcittFalse,
            CrcAlgorithm.Crc16Xmodem => CrcParameters.Crc16Xmodem,
            CrcAlgorithm.Crc16Kermit => CrcParameters.Crc16Kermit,
            CrcAlgorithm.Crc16X25 => CrcParameters.Crc16X25,
            CrcAlgorithm.Crc16Modbus => CrcParameters.Crc16Modbus,
            CrcAlgorithm.Crc16Arc => CrcParameters.Crc16Arc,
            CrcAlgorithm.Crc16Usb => CrcParameters.Crc16Usb,
            CrcAlgorithm.Crc8 => CrcParameters.Crc8,
            CrcAlgorithm.Crc8Maxim => CrcParameters.Crc8Maxim,
            CrcAlgorithm.Crc8SaeJ1850 => CrcParameters.Crc8SaeJ1850,
            CrcAlgorithm.Crc5Usb => CrcParameters.Crc5Usb,
            CrcAlgorithm.Crc24OpenPgp => CrcParameters.Crc24OpenPgp,
            CrcAlgorithm.Crc64Xz => CrcParameters.Crc64Xz,
            CrcAlgorithm.Crc64Ecma182 => CrcParameters.Crc64Ecma182,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, null),
        };
}
