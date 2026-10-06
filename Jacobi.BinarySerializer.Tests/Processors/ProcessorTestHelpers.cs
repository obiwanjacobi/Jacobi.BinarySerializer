using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;

namespace Jacobi.BinarySerializer.Tests.Processors;

/// <summary>Builds schemas that use the 'sys' processors and runs them through a write and read session.</summary>
internal static class ProcessorTestHelpers
{
    public static ExecutionPlan Build(SchemaGroup root)
    {
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        return new ExecutionPlanBuilder(manager).Build(root);
    }

    /// <summary>Property names are expanded to the full 'sys:{id}.{name}' form (enum options stay as-is; they are values, not settings).</summary>
    public static SchemaProcessorRef Ref(string id, params (string Name, string Value)[] properties)
    {
        var processor = new SchemaProcessorRef { Processor = new SchemaProcessorName($"sys.{id}") };
        var key = new ProcessorKey("sys", id);
        processor.PropertyList.AddRange(properties.Select(p => new SchemaProperty
        {
            Name = p.Name.Contains('.') ? p.Name : key.PropertyName(p.Name),
            Value = p.Value
        }));
        return processor;
    }

    /// <summary>Field properties are expanded to the full 'sys:bitpacker.{name}' form unless already prefixed.</summary>
    public static SchemaField Field(string name, SchemaDataType type, SchemaProcessorRef[]? processors = null, params (string Name, string Value)[] properties)
        => new()
        {
            Name = name,
            DataType = type,
            ProcessorsList = [.. processors ?? []],
            PropertyList = [.. properties.Select(p => new SchemaProperty
            {
                Name = p.Name.Contains('.') ? p.Name : new ProcessorKey("sys", "bitpacker").PropertyName(p.Name),
                Value = p.Value
            })]
        };

    public static SchemaGroup Group(string name, SchemaProcessorRef[] processors, params SchemaNode[] children)
    {
        var group = new SchemaGroup { Name = name, ProcessorsList = [.. processors] };
        group.ChildList.AddRange(children);
        return group;
    }

    public static byte[] Write(SchemaGroup root, Dictionary<string, object?> values, out WriteResult result)
    {
        var output = new ArrayBufferWriter<byte>();
        result = new WriterSession(Build(root), output).Write(new DictSource(values));
        return output.WrittenSpan.ToArray();
    }

    /// <summary>Writes the values, asserts success, reads the bytes back and returns both.</summary>
    public static (byte[] Bytes, Dictionary<string, object?> Values) RoundTrip(SchemaGroup root, Dictionary<string, object?> values)
    {
        var plan = Build(root);
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(new DictSource(values)), Is.EqualTo(WriteResult.Success));

        var sink = new DictSink();
        var read = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);
        Assert.That(read, Is.EqualTo(ReadResult.Success));

        return (output.WrittenSpan.ToArray(), sink.Values);
    }
}
