using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

/// <summary>
/// Determines what happens with null values in the serialization process. 
/// This processor is used to handle cases where a value is null, 
/// and it can be configured to either skip the value, 
/// write a default value, or throw an exception. 
/// The NullProcessor is typically used in scenarios where the presence of null 
/// values needs to be explicitly managed during serialization and deserialization.
/// </summary>
internal sealed class NullProcessor : ProcessorBase, IValueProcessor
{
    public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public ProcessorKey Key => new("sys.null");
    public string Name => "Null Value Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [];
}
