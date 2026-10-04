using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class DataTypeCodecTests
{
    [TestCase(SchemaDataType.Boolean, 1)]
    [TestCase(SchemaDataType.Int8, 1)]
    [TestCase(SchemaDataType.UInt8, 1)]
    [TestCase(SchemaDataType.Int16, 2)]
    [TestCase(SchemaDataType.UInt16, 2)]
    [TestCase(SchemaDataType.Int32, 4)]
    [TestCase(SchemaDataType.UInt32, 4)]
    [TestCase(SchemaDataType.Int64, 8)]
    [TestCase(SchemaDataType.UInt64, 8)]
    [TestCase(SchemaDataType.Double, 8)]
    [TestCase(SchemaDataType.DateTime, 8)]
    public void FixedSize_ReturnsWidthInBytes(SchemaDataType type, int expected)
        => Assert.That(DataTypeCodec.FixedSize(type), Is.EqualTo(expected));

    [TestCase(SchemaDataType.String)]
    [TestCase(SchemaDataType.None)]
    public void FixedSize_VariableTypes_AreNull(SchemaDataType type)
        => Assert.That(DataTypeCodec.FixedSize(type), Is.Null);

    [Test]
    public void Encode_Int32_IsLittleEndian()
    {
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.Int32, 0x01020304, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 4, 3, 2, 1 }));
    }

    [Test]
    public void Encode_NegativeInt16_UsesTwosComplement()
    {
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.Int16, (short)-2, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xFE, 0xFF }));
    }

    [TestCase(SchemaDataType.Boolean, true)]
    [TestCase(SchemaDataType.Boolean, false)]
    [TestCase(SchemaDataType.Int8, (sbyte)-5)]
    [TestCase(SchemaDataType.UInt8, (byte)200)]
    [TestCase(SchemaDataType.Int16, (short)-1234)]
    [TestCase(SchemaDataType.UInt16, (ushort)65000)]
    [TestCase(SchemaDataType.Int32, -123456)]
    [TestCase(SchemaDataType.UInt32, 4000000000u)]
    [TestCase(SchemaDataType.Int64, long.MinValue)]
    [TestCase(SchemaDataType.UInt64, ulong.MaxValue)]
    [TestCase(SchemaDataType.Double, 3.14159)]
    public void RoundTrip_PreservesValueAndClrType(SchemaDataType type, object value)
    {
        Assert.That(DataTypeCodec.TryEncode(type, value, out var bytes), Is.True);
        Assert.That(bytes, Has.Length.EqualTo(DataTypeCodec.FixedSize(type)));

        Assert.That(DataTypeCodec.TryDecode(type, bytes, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(decoded, Is.TypeOf(DataTypeCodec.ClrType(type)));
    }

    [Test]
    public void RoundTrip_DateTime_PreservesTicksAsUtc()
    {
        var value = new DateTime(2024, 5, 17, 13, 45, 30, DateTimeKind.Utc).AddTicks(1234);

        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.DateTime, value, out var bytes), Is.True);
        Assert.That(DataTypeCodec.TryDecode(SchemaDataType.DateTime, bytes, out var decoded), Is.True);

        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(((DateTime)decoded!).Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void Encode_ConvertsCompatibleNumericTypes()
    {
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.UInt16, 300, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2C, 0x01 }));
    }

    [Test]
    public void Encode_ValueOutOfRange_Fails()
    {
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.UInt8, 256, out _), Is.False);
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.UInt32, -1, out _), Is.False);
    }

    [Test]
    public void Encode_NullOrWrongKind_Fails()
    {
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.Int32, null, out _), Is.False);
        Assert.That(DataTypeCodec.TryEncode(SchemaDataType.Int32, new object(), out _), Is.False);
    }

    [Test]
    public void Encode_VariableType_Fails()
        => Assert.That(DataTypeCodec.TryEncode(SchemaDataType.String, "abc", out _), Is.False);

    [Test]
    public void Decode_WrongLength_Fails()
        => Assert.That(DataTypeCodec.TryDecode(SchemaDataType.Int32, new byte[] { 1, 2 }, out _), Is.False);

    [Test]
    public void Decode_InvalidDateTimeTicks_Fails()
    {
        var bytes = BitConverter.GetBytes(long.MaxValue);

        Assert.That(DataTypeCodec.TryDecode(SchemaDataType.DateTime, bytes, out _), Is.False);
    }
}
