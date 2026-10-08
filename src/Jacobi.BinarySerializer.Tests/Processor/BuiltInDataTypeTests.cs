using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processor;

public class BuiltInDataTypeTests
{
    private static readonly DataTypeRegistry Registry = DataTypeRegistry.CreateDefault();

    private static DataTypeDescriptor Get(string name) => Registry.Get(new SchemaDataType(name));

    private static bool Encode(string type, object? value, out byte[] bytes)
        => Get(type).Encode!(value, out bytes);

    private static bool Decode(string type, byte[] bytes, out object? value)
        => Get(type).Decode!(bytes, out value);

    [TestCase("Boolean", 1)]
    [TestCase("Int8", 1)]
    [TestCase("UInt8", 1)]
    [TestCase("Int16", 2)]
    [TestCase("UInt16", 2)]
    [TestCase("Int32", 4)]
    [TestCase("UInt32", 4)]
    [TestCase("Int64", 8)]
    [TestCase("UInt64", 8)]
    [TestCase("Double", 8)]
    [TestCase("DateTime", 8)]
    public void FixedSize_ReturnsWidthInBytes(string type, int expected)
        => Assert.That(Get(type).FixedSize, Is.EqualTo(expected));

    [TestCase("String")]
    [TestCase("Object")]
    public void FixedSize_VariableTypes_AreNull(string type)
        => Assert.That(Get(type).FixedSize, Is.Null);

    [Test]
    public void Names_AreCaseInsensitive()
    {
        Assert.That(Registry.TryGet(new SchemaDataType("sys.INT32"), out var upper), Is.True);
        Assert.That(upper, Is.SameAs(Get("int32")));
    }

    [Test]
    public void Encode_Int32_IsLittleEndian()
    {
        Assert.That(Encode("Int32", 0x01020304, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 4, 3, 2, 1 }));
    }

    [Test]
    public void Encode_NegativeInt16_UsesTwosComplement()
    {
        Assert.That(Encode("Int16", (short)-2, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xFE, 0xFF }));
    }

    [TestCase("Boolean", true)]
    [TestCase("Boolean", false)]
    [TestCase("Int8", (sbyte)-5)]
    [TestCase("UInt8", (byte)200)]
    [TestCase("Int16", (short)-1234)]
    [TestCase("UInt16", (ushort)65000)]
    [TestCase("Int32", -123456)]
    [TestCase("UInt32", 4000000000u)]
    [TestCase("Int64", long.MinValue)]
    [TestCase("UInt64", ulong.MaxValue)]
    [TestCase("Double", 3.14159)]
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

        Assert.That(Encode("DateTime", value, out var bytes), Is.True);
        Assert.That(Decode("DateTime", bytes, out var decoded), Is.True);

        Assert.That(decoded, Is.EqualTo(value));
        Assert.That(((DateTime)decoded!).Kind, Is.EqualTo(DateTimeKind.Utc));
    }

    [Test]
    public void Encode_ConvertsCompatibleNumericTypes()
    {
        Assert.That(Encode("UInt16", 300, out var bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x2C, 0x01 }));
    }

    [Test]
    public void Encode_ValueOutOfRange_Fails()
    {
        Assert.That(Encode("UInt8", 256, out _), Is.False);
        Assert.That(Encode("UInt32", -1, out _), Is.False);
    }

    [Test]
    public void Encode_NullOrWrongKind_Fails()
    {
        Assert.That(Encode("Int32", null, out _), Is.False);
        Assert.That(Encode("Int32", new object(), out _), Is.False);
    }

    [Test]
    public void Encode_VariableType_HasNoDefaultRepresentation()
        => Assert.That(Get("String").HasDefaultRepresentation, Is.False);

    [Test]
    public void Decode_WrongLength_Fails()
        => Assert.That(Decode("Int32", [1, 2], out _), Is.False);

    [Test]
    public void Decode_InvalidDateTimeTicks_Fails()
        => Assert.That(Decode("DateTime", BitConverter.GetBytes(long.MaxValue), out _), Is.False);

    [Test]
    public void Parse_Integers_DecimalAndHex()
    {
        Assert.That(Get("UInt16").Parse("0xFF", out var hex), Is.True);
        Assert.That(hex, Is.EqualTo((ushort)255));
        Assert.That(Get("UInt8").Parse("256", out _), Is.False);
    }
}
