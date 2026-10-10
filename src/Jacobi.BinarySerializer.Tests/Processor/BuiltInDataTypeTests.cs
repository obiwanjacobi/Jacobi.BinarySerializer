using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processor;

public class BuiltInDataTypeTests
{
    private static readonly DataTypeRegistry Registry = DataTypeRegistry.CreateDefault();

    private static DataTypeDescriptor Get(string name) => Registry.Get(new SchemaName(name));

    private static bool Encode(string type, object? value, out byte[] bytes)
        => Get(type).Encode!(value, out bytes);

    private static bool Decode(string type, byte[] bytes, out object? value)
        => Get(type).Decode!(bytes, out value);

    [TestCase("sys.boolean", 1)]
    [TestCase("sys.int8", 1)]
    [TestCase("sys.uint8", 1)]
    [TestCase("sys.int16", 2)]
    [TestCase("sys.uint16", 2)]
    [TestCase("sys.int32", 4)]
    [TestCase("sys.uint32", 4)]
    [TestCase("sys.int64", 8)]
    [TestCase("sys.uint64", 8)]
    [TestCase("sys.double", 8)]
    [TestCase("sys.datetime", 8)]
    public void FixedSize_ReturnsWidthInBytes(string type, int expected)
        => Assert.That(Get(type).FixedSize, Is.EqualTo(expected));

    [TestCase("sys.string")]
    [TestCase("sys.object")]
    public void FixedSize_VariableTypes_AreNull(string type)
        => Assert.That(Get(type).FixedSize, Is.Null);

    [Test]
    public void Names_AreCaseInsensitive()
    {
        Assert.That(Registry.TryGet(new SchemaName("sys.INT32"), out var upper), Is.True);
        Assert.That(upper, Is.SameAs(Get("sys.int32")));
    }

    [Test]
    public void Encode_Int32_IsLittleEndian()
    {
        Assert.That(Encode("sys.int32", 0x01020304, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 4, 3, 2, 1 }));
    }

    [Test]
    public void Encode_NegativeInt16_UsesTwosComplement()
    {
        Assert.That(Encode("sys.int16", (short)-2, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xFE, 0xFF }));
    }

    [TestCase("sys.boolean", true)]
    [TestCase("sys.boolean", false)]
    [TestCase("sys.int8", (sbyte)-5)]
    [TestCase("sys.uint8", (byte)200)]
    [TestCase("sys.int16", (short)-1234)]
    [TestCase("sys.uint16", (ushort)65000)]
    [TestCase("sys.int32", -123456)]
    [TestCase("sys.uint32", 4000000000u)]
    [TestCase("sys.int64", long.MinValue)]
    [TestCase("sys.uint64", ulong.MaxValue)]
    [TestCase("sys.double", 3.14159)]
    public void RoundTrip_PreservesValueAndClrType(string type, object value)
    {
        Assert.That(Encode(type, value, out var bytes), Is.True);
        Assert.That(bytes, Has.Length.EqualTo(Get(type).FixedSize));

        Assert.That(Decode(type, bytes, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(decoded, Is.TypeOf(Get(type).ClrType));
    }

    [Test]
    public void RoundTrip_DateTime_PreservesTicksAsUtc()
    {
        var value = new DateTime(2024, 5, 17, 13, 45, 30, DateTimeKind.Utc).AddTicks(1234);

        Assert.That(Encode("sys.datetime", value, out var bytes), Is.True);
        Assert.That(Decode("sys.datetime", bytes, out var decoded), Is.True);

        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(((DateTime)decoded!).Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void Encode_ConvertsCompatibleNumericTypes()
    {
        Assert.That(Encode("sys.uint16", 300, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2C, 0x01 }));
    }

    [Test]
    public void Encode_ValueOutOfRange_Fails()
    {
        Assert.That(Encode("sys.uint8", 256, out _), Is.False);
        Assert.That(Encode("sys.uint32", -1, out _), Is.False);
    }

    [Test]
    public void Encode_NullOrWrongKind_Fails()
    {
        Assert.That(Encode("sys.int32", null, out _), Is.False);
        Assert.That(Encode("sys.int32", new object(), out _), Is.False);
    }

    [Test]
    public void Encode_VariableType_HasNoDefaultRepresentation()
        => Assert.That(Get("sys.string").HasDefaultRepresentation, Is.False);

    [Test]
    public void Decode_WrongLength_Fails()
        => Assert.That(Decode("sys.int32", [1, 2], out _), Is.False);

    [Test]
    public void Decode_InvalidDateTimeTicks_Fails()
        => Assert.That(Decode("sys.datetime", BitConverter.GetBytes(long.MaxValue), out _), Is.False);

    [Test]
    public void Parse_Integers_DecimalAndHex()
    {
        Assert.That(Get("sys.uint16").Parse("0xFF", out var hex), Is.True);
        Assert.That(hex, Is.EqualTo((ushort)255));
        Assert.That(Get("sys.uint8").Parse("256", out _), Is.False);
    }
}
