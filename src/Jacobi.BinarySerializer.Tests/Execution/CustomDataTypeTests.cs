using System.Buffers;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class CustomDataTypeTests
{
    private sealed class Sink : IFieldSink
    {
        public Dictionary<string, object?> Values { get; } = [];
        public void SetField(FieldContext context, LogicalField value) => Values[context.Path] = value.Value;
    }

    private static DataTypeDescriptor BigEndianWord() => new(
        "my.word",
        typeof(ushort),
        (string? text, out object? value) =>
        {
            var ok = ushort.TryParse(text, out var parsed);
            value = parsed;
            return ok;
        })
    {
        FixedSize = 2,
        Encode = (object? value, out byte[] bytes) =>
        {
            var v = Convert.ToUInt16(value);
            bytes = [(byte)(v >> 8), (byte)v];
            return true;
        },
        Decode = (byte[] bytes, out object? value) =>
        {
            value = (ushort)((bytes[0] << 8) | bytes[1]);
            return true;
        }
    };

    private static ExecutionPlan Build(SchemaGroup root, DataTypeRegistry registry)
        => new ExecutionPlanBuilder(new ProcessorManager(), registry).Build(root);

    private static SchemaGroup Root(string type)
    {
        var root = new SchemaGroup { Name = "Root" };
        root.MemberList.Add(new SchemaField { Name = "A", DataType = type });
        return root;
    }

    [Test]
    public void CustomType_WriteAndRead_RoundTripsThroughDefaultRepresentation()
    {
        var registry = DataTypeRegistry.CreateDefault();
        registry.Register(BigEndianWord());
        var plan = Build(Root("my.word"), registry);

        var output = new ArrayBufferWriter<byte>();
        var written = new WriterSession(plan, output).Write(new DictSource(new() { ["Root.A"] = (ushort)0x0102 }));

        Assert.That(written, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));

        var sink = new Sink();
        var read = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);

        Assert.That(read, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.A"], Is.EqualTo((ushort)0x0102));
    }

    [Test]
    public void UnregisteredType_PlanBuild_ReportsError()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => Build(Root("my.unknown"), DataTypeRegistry.CreateDefault()));

        Assert.That(ex!.Errors[0], Does.Contain("Root.A").And.Contain("my.unknown").And.Contain("not registered"));
    }

    [Test]
    public void CustomType_NotInRegistry_IsNotRegisteredError()
    {
        Assert.Throws<ExecutionPlanException>(() => Build(Root("my.word"), DataTypeRegistry.CreateDefault()));
    }
}
