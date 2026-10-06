using Jacobi.BinarySerializer.Processor;

namespace Jacobi.BinarySerializer.Processors;

public sealed class ProcessorFactory : IProcessorFactory
{
    private readonly static Dictionary<string, IProcessor> Processors = new()
    {
        // Value Processors
        { "sys.nullable", new NullableProcessor() },
        { "sys.enum", new EnumProcessor() },
        { "sys.scale", new ScaleProcessor() },
        { "sys.map", new MapProcessor() },
        // Field Processors
        { "sys.varint", new VarIntProcessor() },
        { "sys.string", new StringProcessor() },
        // Loayout Processors
        { "sys.bitpacker", new BitPackerProcessor() },
        { "sys.bytepacker", new BytePackerProcessor() },
        { "sys.align", new AlignProcessor() },
    };

    public string Namespace => "sys";

    public IProcessor? CreateProcessor(string id)
    {
        return Processors.GetValueOrDefault($"{Namespace}.{id}");
    }
}
