using Jacobi.BinarySerializer.Codecs;

namespace Jacobi.BinarySerializer.Tests.Codecs;

public class CrcCodecTests
{
    private static readonly byte[] Check = "123456789"u8.ToArray();

    // Check values from the reveng CRC catalog.
    [TestCase("crc32", 0xCBF43926UL)]
    [TestCase("crc32c", 0xE3069283UL)]
    [TestCase("crc32-bzip2", 0xFC891918UL)]
    [TestCase("crc32-mpeg2", 0x0376E6E7UL)]
    [TestCase("crc16-ccitt-false", 0x29B1UL)]
    [TestCase("crc16-xmodem", 0x31C3UL)]
    [TestCase("crc16-kermit", 0x2189UL)]
    [TestCase("crc16-x25", 0x906EUL)]
    [TestCase("crc16-modbus", 0x4B37UL)]
    [TestCase("crc16-arc", 0xBB3DUL)]
    [TestCase("crc16-usb", 0xB4C8UL)]
    [TestCase("crc8", 0xF4UL)]
    [TestCase("crc8-maxim", 0xA1UL)]
    [TestCase("crc8-sae-j1850", 0x4BUL)]
    [TestCase("crc5-usb", 0x19UL)]
    [TestCase("crc24-openpgp", 0x21CF02UL)]
    [TestCase("crc64-xz", 0x995DC9BBDF1939FAUL)]
    [TestCase("crc64-ecma-182", 0x6C40DF5F0B497347UL)]
    public void Compute_CheckValue(string preset, ulong expected)
        => Assert.That(CrcCodec.Create(preset).Compute(Check), Is.EqualTo(expected));

    [Test]
    public void Update_Incremental_EqualsOneShot()
    {
        var codec = CrcCodec.Create("crc32");
        var state = codec.Update(codec.Initial, "IE"u8);
        state = codec.Update(state, "ND"u8);
        Assert.That(codec.Finish(state), Is.EqualTo(0xAE426082UL));
    }

    [Test]
    public void Create_UnknownPreset_Throws()
        => Assert.Throws<ArgumentException>(() => CrcCodec.Create("nope"));
}
