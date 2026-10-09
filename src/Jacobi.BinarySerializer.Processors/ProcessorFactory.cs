using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

public sealed class ProcessorFactory : IProcessorFactory
{
    private readonly static Dictionary<string, ProcessorBase> Processors = new()
    {
        // Value Processors
        { "sys.nullable", new NullableProcessor() },
        { "sys.enum", new EnumProcessor() },
        { "sys.scale", new ScaleProcessor() },
        { "sys.map", new MapProcessor() },
        // Field Processors
        { "sys.varint", new VarIntProcessor() },
        { "sys.string", new StringProcessor() },
        { "sys.bits", new BitFieldProcessor() },
        // Layout Processors
        { "sys.bitpacker", new BitPackerProcessor() },
        { "sys.bytepacker", new BytePackerProcessor() },
        { "sys.align", new AlignProcessor() },
        { "sys.crc", new CrcProcessor() },
    };

    public string Namespace => "sys";

    public IEnumerable<DataTypeDescriptor> DataTypes { get; }
        = [.. Processors.Values.SelectMany(p => p.DataTypes)];

    public IProcessor? CreateProcessor(string id)
    {
        return (IProcessor?)Processors.GetValueOrDefault($"{Namespace}.{id}");
    }
}
