using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

public sealed class ProcessorFactory : IProcessorFactory
{
    private readonly static Dictionary<string, ProcessorBase> ProcessorMap = new()
    {
        // Value Processors
        { "sys.null", new NullProcessor() },
        { "sys.scale", new ScaleProcessor() },
        { "sys.map", new MapProcessor() },
        { "sys.bits", new BitsProcessor() },
        // Field Processors
        { "sys.varint", new VarIntProcessor() },
        { "sys.string", new StringProcessor() },
        { "sys.bitslicer", new BitSlicerProcessor() },
        // Layout Processors
        { "sys.bitpacker", new BitPackerProcessor() },
        { "sys.bytepacker", new BytePackerProcessor() },
        { "sys.align", new AlignProcessor() },
        { "sys.crc", new CrcProcessor() },
    };

    public string Namespace => "sys";

    public IEnumerable<string> Processors
        => ProcessorMap.Keys.Select(p => p[4..]);

    public IEnumerable<DataTypeDescriptor> DataTypes { get; }
        = [.. ProcessorMap.Values.SelectMany(p => p.DataTypes)];

    public IProcessor? CreateProcessor(string id)
    {
        return (IProcessor?)ProcessorMap.GetValueOrDefault($"{Namespace}.{id}");
    }
}
