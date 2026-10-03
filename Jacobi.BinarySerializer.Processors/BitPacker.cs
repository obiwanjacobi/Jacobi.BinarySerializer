using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

internal sealed class BitPacker : IFieldProcessor
{
    public EncodedField Write(LogicalField field, FieldProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public LogicalField Read(EncodedField field, FieldProcessorContext context)
    {
        throw new NotImplementedException();
    }

    public ProcessorKey Key => new("sys.bitpacker");
    public string Name => "Bit-Packer Processor";
    public PipelineStage Stage => PipelineStage.Semantic;
    public IReadOnlyList<PropertyDescriptor> Properties => [];
}
