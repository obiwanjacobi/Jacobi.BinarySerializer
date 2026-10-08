using Jacobi.BinarySerializer.Codecs;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processor;

public class ProcessorPropertiesDescriptorTests
{
    private static ProcessorProperties Props(params (string Name, string Value)[] values)
        => new([.. values.Select(v => new SchemaProperty { Name = v.Name, Value = v.Value })], new ProcessorKey("sys", "test"), Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault());

    [Test]
    public void GetTyped_Present_ParsesValue()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: false);

        Assert.That(Props(("count", "42")).Get<int>(descriptor), Is.EqualTo(42));
    }

    [Test]
    public void GetTyped_FullName_Resolves()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: false);

        Assert.That(Props(("sys.test.count", "7")).Get<int>(descriptor), Is.EqualTo(7));
    }

    [Test]
    public void GetOrDefault_Absent_ReturnsDefault()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: false);

        Assert.That(Props().GetOrDefault<int>(descriptor), Is.EqualTo(0));
        Assert.That(Props().GetOrDefault(descriptor, 5), Is.EqualTo(5));
        Assert.That(Props(("count", "9")).GetOrDefault(descriptor, 5), Is.EqualTo(9));
    }

    [Test]
    public void TryGet_AbsentAndPresent()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: false);

        Assert.That(Props().TryGet<int>(descriptor, out _), Is.False);
        Assert.That(Props(("count", "3")).TryGet<int>(descriptor, out var value), Is.True);
        Assert.That(value, Is.EqualTo(3));
        Assert.That(Props(("count", "x")).TryGet<int>(descriptor, out _), Is.False);
    }

    [Test]
    public void Get_OptionalAbsent_Throws()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: false);

        Assert.Throws<InvalidOperationException>(() => Props().Get<int>(descriptor));
    }

    [Test]
    public void GetTyped_RequiredAbsent_Throws()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: true);

        Assert.Throws<InvalidOperationException>(() => Props().Get<int>(descriptor));
    }

    [Test]
    public void GetTyped_InvalidValue_Throws()
    {
        var descriptor = new PropertyDescriptor("count", "int32", isRequired: false);

        Assert.Throws<InvalidOperationException>(() => Props(("count", "abc")).Get<int>(descriptor));
    }

    [Test]
    public void GetTyped_TypeMismatch_Throws()
    {
        var descriptor = new PropertyDescriptor("count", "string", isRequired: false);

        Assert.Throws<InvalidOperationException>(() => Props(("count", "1")).Get<int>(descriptor));
    }

    [Test]
    public void GetString_Present_ReturnsText()
    {
        var descriptor = new PropertyDescriptor("name", "string", isRequired: false);

        Assert.That(Props(("name", "abc")).Get<string>(descriptor), Is.EqualTo("abc"));
    }

    [Test]
    public void GetString_OptionalAbsent_ReturnsNull()
    {
        var descriptor = new PropertyDescriptor("name", "string", isRequired: false);

        Assert.That(Props().GetOrDefault<string>(descriptor), Is.Null);
    }

    [Test]
    public void GetString_RequiredAbsent_Throws()
    {
        var descriptor = new PropertyDescriptor("name", "string", isRequired: true);

        Assert.Throws<InvalidOperationException>(() => Props().GetOrDefault<string>(descriptor));
        Assert.That(Props().TryGet<string>(descriptor, out _), Is.False);
        Assert.That(() => Props().Get<string>(descriptor), Throws.InvalidOperationException.With.Message.Contains("required"));
    }

    [Test]
    public void GetEnum_ParsesMemberName()
    {
        var descriptor = new PropertyDescriptor("order", "sys.endianness", isRequired: false);

        Assert.That(Props(("order", "big")).Get<Endianness>(descriptor), Is.EqualTo(Endianness.Big));
        Assert.Throws<InvalidOperationException>(() => Props(("order", "middle")).Get<Endianness>(descriptor));
    }
}
