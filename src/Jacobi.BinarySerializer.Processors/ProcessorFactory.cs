using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

public sealed class ProcessorFactory : IProcessorFactory
{
    private readonly static Dictionary<string, ProcessorBase> ProcessorMap = new()
    {
        // Value Processors
        { "null", new NullProcessor() },
        { "scale", new ScaleProcessor() },
        { "map", new MapProcessor() },
        { "bits", new BitsProcessor() },
        // Field Processors
        { "varint", new VarIntProcessor() },
        { "string", new StringProcessor() },
        { "bitslicer", new BitSlicerProcessor() },
        // Layout Processors
        { "bitpacker", new BitPackerProcessor() },
        { "bytepacker", new BytePackerProcessor() },
        { "align", new AlignProcessor() },
        { "crc", new CrcProcessor() },
    };

    public string Namespace => "sys";

    public IEnumerable<string> Processors
        => ProcessorMap.Keys;

    public IEnumerable<DataTypeDescriptor> DataTypes { get; }
        = [.. ProcessorMap.Values.SelectMany(p => p.DataTypes)];

    public IProcessor? CreateProcessor(string id)
    {
        return (IProcessor?)ProcessorMap.GetValueOrDefault(id);
    }
}
