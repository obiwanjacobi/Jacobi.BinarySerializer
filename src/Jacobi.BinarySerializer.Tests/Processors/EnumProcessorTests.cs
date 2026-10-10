using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class EnumProcessorTests
{
    private static SchemaGroup CreateRoot()
        => Group("Root", [],
            Field("Color", "sys.int32", [Ref("enum", ("Red", "1"), ("Green", "2"), ("Blue", "4"))]));

    [TestCase("Red", 1)]
    [TestCase("Green", 2)]
    [TestCase("Blue", 4)]
    public void RoundTrip_PreservesTheOptionName_AndWritesItsNumber(string option, int number)
    {
        var (bytes, values) = RoundTrip(CreateRoot(), new() { ["Root.Color"] = option });

        Assert.That(values["Root.Color"], Is.EqualTo(option));
        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(number)));
    }

    [Test]
    public void Write_UnknownOption_Throws()
    {
        Assert.That(() => Write(CreateRoot(), new() { ["Root.Color"] = "Purple" }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
