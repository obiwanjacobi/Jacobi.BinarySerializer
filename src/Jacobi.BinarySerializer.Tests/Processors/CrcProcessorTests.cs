using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class CrcProcessorTests
{
    private static readonly byte[] Check = "123456789"u8.ToArray();

    private static SchemaGroup CrcGroup(params (string Name, string Value)[] properties)
    {
        var data = new SchemaField { Name = "Data", DataType = "sys.bytes", ByteLength = Check.Length };
        return Group("Root", [Ref("crc", properties)], data);
    }

    private static Dictionary<string, object?> CheckValues() => new() { ["Root.Data"] = Check };

    private static byte[] Expected(ulong crc, int byteLength, bool bigEndian = true)
    {
        var bytes = new byte[byteLength];
        for (var i = 0; i < byteLength; i++)
        {
            var shift = 8 * (bigEndian ? byteLength - 1 - i : i);
            bytes[i] = (byte)(crc >> shift);
        }
        return [.. Check, .. bytes];
    }

    [TestCase("crc32", 0xCBF43926UL, 4)]
    [TestCase("crc32c", 0xE3069283UL, 4)]
    [TestCase("crc32-bzip2", 0xFC891918UL, 4)]
    [TestCase("crc16-modbus", 0x4B37UL, 2)]
    [TestCase("crc16-xmodem", 0x31C3UL, 2)]
    public void RoundTrip_Algorithm_AppendsTheKnownCrc(string algorithm, ulong crc, int byteLength)
    {
        var (bytes, read) = RoundTrip(CrcGroup(("algorithm", algorithm)), CheckValues());

        Assert.That(bytes, Is.EqualTo(Expected(crc, byteLength)));
        Assert.That((byte[])read["Root.Data"]!, Is.EqualTo(Check));
    }

    [Test]
    public void RoundTrip_LittleEndianByteOrder_StoresTheCrcReversed()
    {
        var (bytes, _) = RoundTrip(CrcGroup(("algorithm", "crc32"), ("byteorder", "little")), CheckValues());

        Assert.That(bytes, Is.EqualTo(Expected(0xCBF43926UL, 4, bigEndian: false)));
    }

    [Test]
    public void RoundTrip_CustomParameters_OverrideThePreset()
    {
        var (bytes, _) = RoundTrip(
            CrcGroup(("width", "16"), ("poly", "0x1021"), ("init", "0xFFFF"), ("refin", "false"), ("refout", "false"), ("xorout", "0")),
            CheckValues());

        Assert.That(bytes, Is.EqualTo(Expected(0x29B1UL, 2)));
    }

    [Test]
    public void Read_CorruptCrc_Throws()
    {
        var plan = Build(CrcGroup(("algorithm", "crc32")));
        var bytes = Expected(0xCBF43926UL, 4);
        bytes[^1] ^= 0xFF;

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new DictSink()), Throws.Exception);
    }

    [Test]
    public void Read_CorruptData_Throws()
    {
        var plan = Build(CrcGroup(("algorithm", "crc32")));
        var bytes = Expected(0xCBF43926UL, 4);
        bytes[0] ^= 0x01;

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new DictSink()), Throws.Exception);
    }

    [Test]
    public void Read_TruncatedCrc_ThrowsOrNeedsMoreData()
    {
        var plan = Build(CrcGroup(("algorithm", "crc32")));
        var bytes = Expected(0xCBF43926UL, 4)[..^2];

        ReadResult? result = null;
        try
        {
            result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new DictSink());
        }
        catch (Exception)
        {
            return;
        }

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Build_UnknownAlgorithm_Fails()
        => Assert.That(() => Build(CrcGroup(("algorithm", "no-such-crc"))), Throws.Exception);

    [TestCase("width", "0")]
    [TestCase("width", "65")]
    [TestCase("poly", "not-a-number")]
    [TestCase("init", "0xZZ")]
    [TestCase("poly", "0")]
    [TestCase("poly", "0x100000000")]
    [TestCase("init", "0x100000000")]
    [TestCase("xorout", "0x100000000")]
    public void Write_InvalidParameter_Fails(string name, string value)
    {
        var root = CrcGroup(("algorithm", "crc32"), (name, value));

        Assert.That(() => Write(root, CheckValues(), out _), Throws.Exception);
    }
}
