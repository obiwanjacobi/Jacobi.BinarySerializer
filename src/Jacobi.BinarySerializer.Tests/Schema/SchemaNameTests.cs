using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Schema;

public class SchemaNameTests
{
    [Test]
    public void Equals_DifferentCasing_IsEqual()
    {
        var a = new SchemaName("Sys.Int32");
        var b = new SchemaName("sys.int32");

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a == b, Is.True);
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        Assert.That(a.Equals("SYS.INT32"), Is.True);
    }

    [Test]
    public void Equals_ReferenceAndPlainName_AreDifferent()
    {
        Assert.That(new SchemaName("ref:aligned"), Is.Not.EqualTo(new SchemaName("aligned")));
    }

    [Test]
    public void Ctor_WithoutNamespace_KeepsNameAsIs()
    {
        var name = new SchemaName("Int32");

        Assert.That(name.Namespace, Is.Empty);
        Assert.That(name.FullName, Is.EqualTo("Int32"));
    }

    [Test]
    public void Registry_ShortBuiltInName_IsNotResolved()
    {
        var registry = DataTypeRegistry.CreateDefault();

        Assert.That(registry.TryGet(new SchemaName("Int32"), out _), Is.False);
        Assert.That(registry.TryGet(new SchemaName("SYS.INT32"), out _), Is.True);
    }

    [Test]
    public void Registry_UnqualifiedBase_ResolvesToDefInSameDocument()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var a = new SchemaDataTypeDef { Name = "A", BasedOn = "sys.int32", Scale = 10m };
        var b = new SchemaDataTypeDef { Name = "B", BasedOn = "A" };
        var document = new SchemaDocument
        {
            Name = "Doc",
            DataTypeDefs = [b, a],
            NodeDefs = [],
            ProcessorDefs = [],
            Includes = [],
            Roots = [],
            Groups = [],
            Fields = []
        };

        registry.RegisterDataTypeDefs([document]);

        Assert.That(registry.Get(new SchemaName("Doc.B")).Scale, Is.EqualTo(10m));
    }

    [Test]
    public void Registry_UnqualifiedBaseWithoutLocalDef_Throws()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var a = new SchemaDataTypeDef { Name = "A", BasedOn = "Int32" };
        var document = new SchemaDocument
        {
            Name = "Doc",
            DataTypeDefs = [a],
            NodeDefs = [],
            ProcessorDefs = [],
            Includes = [],
            Roots = [],
            Groups = [],
            Fields = []
        };

        Assert.Throws<InvalidOperationException>(() => registry.RegisterDataTypeDefs([document]));
    }
}
