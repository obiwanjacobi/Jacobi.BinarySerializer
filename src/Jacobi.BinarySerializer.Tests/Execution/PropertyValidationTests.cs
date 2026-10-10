using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class PropertyValidationTests
{
    private sealed class TypedProcessor(string dataType) : IValueProcessor
    {
        public ProcessorKey Key => new("pv", "typed");
        public string Name => "Typed";
        public PipelineStage Stage => PipelineStage.Semantic;
        public IReadOnlyList<PropertyDescriptor> Properties => [new("amount", dataType, false)];

        public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context) => logicalValue;
        public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context) => logicalValue;
    }

    private sealed class Factory(string dataType) : IProcessorFactory
    {
        public string Namespace => "pv";
        public IProcessor? CreateProcessor(string id) => id == "typed" ? new TypedProcessor(dataType) : null;
    }

    private static ExecutionPlan Build(string dataType, string? value)
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory(dataType));

        var processor = new SchemaProcessorRef { Processor = new SchemaName("pv.typed") };
        if (value is not null)
        {
            processor.PropertyList.Add(new SchemaProperty { Name = "amount", Value = value });
        }

        var root = new SchemaGroup { Name = "Root" };
        root.MemberList.Add(new SchemaField { Name = "A", DataType = "sys.int32", ProcessorsList = [processor] });
        return new ExecutionPlanBuilder(manager).Build(root);
    }

    [Test]
    public void ValidValue_Builds()
        => Assert.DoesNotThrow(() => Build("sys.int32", "5"));

    [Test]
    public void AbsentValue_Builds()
        => Assert.DoesNotThrow(() => Build("sys.int32", null));

    [Test]
    public void UnparsableValue_FailsPlan()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => Build("sys.int32", "abc"));
        Assert.That(ex!.Message, Does.Contain("amount"));
    }

    [Test]
    public void UnregisteredType_FailsPlan()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => Build("my.unknown", "1"));
        Assert.That(ex!.Message, Does.Contain("my.unknown"));
    }
}
