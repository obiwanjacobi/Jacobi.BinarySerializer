using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class VarIntProcessor : IFieldProcessor
{
    public EncodedField Write(LogicalField field, FieldProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public LogicalField Read(EncodedField field, FieldProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public ProcessorKey Key => new("sys.varint");
    public string Name => "Variable Integer Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [];
}