using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processor;

public class DataTypeRegistryTests
{
    private static DataTypeDescriptor Custom(string name)
        => new(name, typeof(int), (string? text, out object? value) =>
        {
            var ok = int.TryParse(text, out var parsed);
            value = parsed;
            return ok;
        });

    [Test]
    public void CreateDefault_ContainsBuiltIns()
    {
        var registry = DataTypeRegistry.CreateDefault();

        Assert.That(registry.TryGet(new SchemaDataType("Int32"), out _), Is.True);
        Assert.That(registry.TryGet(new SchemaDataType("sys.string"), out _), Is.True);
        Assert.That(registry.Types.Count(), Is.GreaterThan(10));
    }

    [Test]
    public void Register_Duplicate_Throws()
    {
        var registry = new DataTypeRegistry();
        registry.Register(Custom("my.a"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Custom("MY.A")));
    }

    [Test]
    public void Register_BuiltInName_Throws()
    {
        var registry = DataTypeRegistry.CreateDefault();

        Assert.Throws<InvalidOperationException>(() => registry.Register(Custom("sys.int32")));
    }

    [Test]
    public void Get_CaseInsensitive()
    {
        var registry = new DataTypeRegistry();
        var descriptor = Custom("my.a");
        registry.Register(descriptor);

        Assert.That(registry.Get(new SchemaDataType("MY.A")), Is.SameAs(descriptor));
    }

    [Test]
    public void Get_Unregistered_Throws()
    {
        var registry = new DataTypeRegistry();

        Assert.Throws<KeyNotFoundException>(() => registry.Get(new SchemaDataType("my.none")));
    }

    [Test]
    public void TryGet_Unregistered_ReturnsFalse()
    {
        var registry = new DataTypeRegistry();

        Assert.That(registry.TryGet(new SchemaDataType("my.none"), out var descriptor), Is.False);
        Assert.That(descriptor, Is.Null);
    }

    [Test]
    public void ReadOnlyView_ExposesLookupOnly()
    {
        IDataTypeRegistry view = DataTypeRegistry.CreateDefault();

        Assert.That(view.Get(new SchemaDataType("bytes")).Name.FullName, Is.EqualTo("sys.bytes"));
    }
}
