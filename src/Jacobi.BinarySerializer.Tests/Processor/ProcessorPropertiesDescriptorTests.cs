using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processor;

public class ProcessorPropertiesDescriptorTests
{
    private static ProcessorProperties Props(params (string Name, string Value)[] values)
        => new([.. values.Select(v => new SchemaProperty { Name = v.Name, Value = v.Value })], new ProcessorKey("sys", "test"));

    [Test]
    public void GetTyped_Present_ParsesValue()
    {
        var descriptor = new PropertyDescriptor("count", typeof(int), isRequired: false);

        Assert.That(Props(("count", "42")).Get<int>(descriptor), Is.EqualTo(42));
    }

    [Test]
    public void GetTyped_FullName_Resolves()
    {
        var descriptor = new PropertyDescriptor("count", typeof(int), isRequired: false);

        Assert.That(Props(("sys.test.count", "7")).Get<int>(descriptor), Is.EqualTo(7));
    }

    [Test]
    public void GetTyped_OptionalAbsent_ReturnsDefault()
    {
        var descriptor = new PropertyDescriptor("count", typeof(int), isRequired: false);

        Assert.That(Props().Get<int>(descriptor), Is.EqualTo(0));
    }

    [Test]
    public void GetTyped_RequiredAbsent_Throws()
    {
        var descriptor = new PropertyDescriptor("count", typeof(int), isRequired: true);

        Assert.Throws<InvalidOperationException>(() => Props().Get<int>(descriptor));
    }

    [Test]
    public void GetTyped_InvalidValue_Throws()
    {
        var descriptor = new PropertyDescriptor("count", typeof(int), isRequired: false);

        Assert.Throws<InvalidOperationException>(() => Props(("count", "abc")).Get<int>(descriptor));
    }

    [Test]
    public void GetTyped_TypeMismatch_Throws()
    {
        var descriptor = new PropertyDescriptor("count", typeof(string), isRequired: false);

        Assert.Throws<InvalidOperationException>(() => Props(("count", "1")).Get<int>(descriptor));
    }

    [Test]
    public void GetString_Present_ReturnsText()
    {
        var descriptor = new PropertyDescriptor("name", typeof(string), isRequired: false);

        Assert.That(Props(("name", "abc")).Get(descriptor), Is.EqualTo("abc"));
    }

    [Test]
    public void GetString_OptionalAbsent_ReturnsNull()
    {
        var descriptor = new PropertyDescriptor("name", typeof(string), isRequired: false);

        Assert.That(Props().Get(descriptor), Is.Null);
    }

    [Test]
    public void GetString_RequiredAbsent_Throws()
    {
        var descriptor = new PropertyDescriptor("name", typeof(string), isRequired: true);

        Assert.Throws<InvalidOperationException>(() => Props().Get(descriptor));
    }
}
