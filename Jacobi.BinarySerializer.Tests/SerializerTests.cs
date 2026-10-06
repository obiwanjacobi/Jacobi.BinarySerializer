using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests;

public class SerializerTests
{
    [Test]
    public void GetPlan_SameSchema_ReturnsCachedInstance()
    {
        var serializer = CreateSerializer("Main", "Root");

        var plan1 = serializer.GetPlan(new SchemaName("Main.Root"));
        var plan2 = serializer.GetPlan(new SchemaName("Main.Root"));

        Assert.That(plan2, Is.SameAs(plan1));
    }

    [Test]
    public void GetPlan_DifferentSchemas_ReturnsDifferentPlans()
    {
        var serializer = CreateSerializer("Main", "RootA", "RootB");

        var planA = serializer.GetPlan(new SchemaName("Main.RootA"));
        var planB = serializer.GetPlan(new SchemaName("Main.RootB"));

        Assert.That(planB, Is.Not.SameAs(planA));
    }

    [Test]
    public void GetPlan_Concurrent_ReturnsSingleInstance()
    {
        var serializer = CreateSerializer("Main", "Root");

        var plans = Enumerable.Range(0, 32)
            .AsParallel()
            .Select(_ => serializer.GetPlan(new SchemaName("Main.Root")))
            .ToList();

        Assert.That(plans.Distinct().Count(), Is.EqualTo(1));
    }

    [Test]
    public void PrepareAll_CachesPlansForAllRoots()
    {
        var serializer = CreateSerializer("Main", "RootA", "RootB");
        Assert.That(serializer.CachedPlanCount, Is.EqualTo(0));

        serializer.PrepareAll();

        Assert.That(serializer.CachedPlanCount, Is.EqualTo(2));
    }

    [Test]
    public void PrepareAll_UnknownSchemaStillThrows()
    {
        var serializer = CreateSerializer("Main", "Root");

        serializer.PrepareAll();

        Assert.Throws<InvalidOperationException>(() => serializer.GetPlan(new SchemaName("Main.Missing")));
    }

    [Test]
    public void Builder_WithoutSchemas_Throws()
    {
        var builder = new SerializerBuilder().AddProcessors(new ProcessorManager());

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Test]
    public void Builder_WithoutProcessors_Throws()
    {
        var builder = new SerializerBuilder().AddSchemas(new SchemaSet());

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Test]
    public void Builder_AfterBuild_CannotBeModified()
    {
        var builder = new SerializerBuilder()
            .AddSchemas(new SchemaSet())
            .AddProcessors(new ProcessorManager());
        builder.Build();

        Assert.Throws<InvalidOperationException>(() => builder.AddSchemas(new SchemaSet()));
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private static Serializer CreateSerializer(string documentName, params string[] rootNames)
    {
        var roots = rootNames.Select(n => new SchemaGroup { Name = n, ProcessorsList = [] }).ToList();
        var document = new SchemaDocument
        {
            Name = documentName,
            Roots = roots,
            Groups = roots,
            Fields = [],
            ChildList = [.. roots],
            TypeDefs = [],
            ProcessorDefs = [],
            Includes = []
        };

        var schemas = new SchemaSet();
        schemas.AddDocument(document);
        schemas.Compile();

        return new SerializerBuilder()
            .AddSchemas(schemas)
            .AddProcessors(new ProcessorManager())
            .Build();
    }
}
