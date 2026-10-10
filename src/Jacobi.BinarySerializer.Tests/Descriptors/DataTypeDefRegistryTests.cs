using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Descriptors;

public class DataTypeDefRegistryTests
{
    private static SchemaDocument Document(string name, params SchemaDataTypeDef[] defs)
        => new()
        {
            Name = name,
            DataTypeDefs = defs,
            NodeDefs = [],
            ProcessorDefs = [],
            Includes = [],
            Roots = [],
            Groups = [],
            Fields = []
        };

    [Test]
    public void Register_DefBasedOnDef_InheritsAndOverridesFacets()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var a = new SchemaDataTypeDef { Name = "A", BasedOn = "sys.int32", Scale = 10m, Min = 0m, Max = 100m };
        var b = new SchemaDataTypeDef { Name = "B", BasedOn = "Doc.A", Max = 50m, Shift = 2m };

        registry.RegisterDataTypeDefs([Document("Doc", b, a)]);

        var derived = registry.Get(new SchemaName("Doc.B"));
        Assert.That(derived.Scale, Is.EqualTo(10m));
        Assert.That(derived.Min, Is.EqualTo(0m));
        Assert.That(derived.Max, Is.EqualTo(50m));
        Assert.That(derived.Shift, Is.EqualTo(2m));
        Assert.That(derived.ClrType, Is.EqualTo(registry.Get(new SchemaName("sys.int32")).ClrType));
    }

    [Test]
    public void Register_DefInOtherDocument_IsUsableAsBase()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var shared = Document("Shared", new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 100m });
        var main = Document("Main", new SchemaDataTypeDef { Name = "Body", BasedOn = "Shared.Celsius", Shift = 1m });

        registry.RegisterDataTypeDefs([main, shared]);

        Assert.That(registry.Get(new SchemaName("Main.Body")).Scale, Is.EqualTo(100m));
    }

    [Test]
    public void Register_CircularDefs_Throws()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var a = new SchemaDataTypeDef { Name = "A", BasedOn = "Doc.B" };
        var b = new SchemaDataTypeDef { Name = "B", BasedOn = "Doc.A" };

        Assert.Throws<InvalidOperationException>(() => registry.RegisterDataTypeDefs([Document("Doc", a, b)]));
    }

    [Test]
    public void Register_UnknownBase_Throws()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var a = new SchemaDataTypeDef { Name = "A", BasedOn = "Nope.Missing" };

        Assert.Throws<InvalidOperationException>(() => registry.RegisterDataTypeDefs([Document("Doc", a)]));
    }

    [Test]
    public void Register_CalledTwice_DoesNotThrow()
    {
        var registry = DataTypeRegistry.CreateDefault();
        var documents = new[] { Document("Doc", new SchemaDataTypeDef { Name = "A", BasedOn = "sys.int32" }) };

        registry.RegisterDataTypeDefs(documents);

        Assert.DoesNotThrow(() => registry.RegisterDataTypeDefs(documents));
    }
}
