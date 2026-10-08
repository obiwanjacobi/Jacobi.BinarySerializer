using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class NullableProcessor : ProcessorBase, IValueProcessor
{
    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public ProcessorKey Key => new("sys.nullable");
    public string Name => "Nullable Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [];
}
