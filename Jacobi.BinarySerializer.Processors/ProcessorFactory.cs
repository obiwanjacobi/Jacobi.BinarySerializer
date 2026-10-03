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
        // Field Processors
        { "sys.bitpacker", new BitPacker() },
        { "sys.varint", new VarIntProcessor() },
    };

    public string Namespace => "sys";

    public IProcessor? CreateProcessor(string id)
    {
        return null;
    }
}
