using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.IntegrationTests.Png;

public class PngSchemaTests
{
    [Test]
    public void Schema_LoadsCompilesAndBuildsPlan()
    {
        var schemaSet = new SchemaSet();
        schemaSet.LoadFile(TestFiles.Path("Png/png.json"));
        schemaSet.Compile();

        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());

        var serializer = new SerializerBuilder()
            .AddSchemas(schemaSet)
            .AddProcessors(manager)
            .Build();

        var plan = serializer.GetPlan(new SchemaName("Png.Png"));

        Assert.That(plan, Is.Not.Null);
    }
}
