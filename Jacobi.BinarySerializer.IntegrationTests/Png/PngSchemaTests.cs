using Jacobi.BinarySerializer.Execution;
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
        var document = schemaSet.LoadFromJson(TestFiles.ReadText("Png/png.json"));
        schemaSet.Compile();

        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        var plan = new ExecutionPlanBuilder(manager).Build(document.Roots.Single());

        Assert.That(plan, Is.Not.Null);
    }
}
