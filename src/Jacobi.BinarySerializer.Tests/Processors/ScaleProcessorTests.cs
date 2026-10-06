using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class ScaleProcessorTests
{
    private static SchemaGroup CreateRoot(string scale = "100")
        => Group("Root", [],
            Field("Temperature", SchemaDataType.Int32, [Ref("scale", ("scale", scale))]));

    [Test]
    public void RoundTrip_ScalesToAnIntegerOnTheWire_AndBack()
    {
        var (bytes, values) = RoundTrip(CreateRoot(), new() { ["Root.Temperature"] = 12.34m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(1234)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(12.34m));
    }

    [Test]
    public void RoundTrip_NegativeValue()
    {
        var (bytes, values) = RoundTrip(CreateRoot("10"), new() { ["Root.Temperature"] = -5.5m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(-55)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(-5.5m));
    }

    [Test]
    public void Write_MissingScaleProperty_Throws()
    {
        var root = Group("Root", [], Field("Temperature", SchemaDataType.Int32, [Ref("scale")]));

        Assert.That(() => Write(root, new() { ["Root.Temperature"] = 1m }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
