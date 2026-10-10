using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class FieldChainTests
{
    private const string Ns = "fc";

    private static ExecutionPlan Build(SchemaGroup root, List<string> log)
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory(log));
        return new ExecutionPlanBuilder(manager).Build(root);
    }

    private static SchemaProcessorRef Ref(string id)
        => new() { Processor = new SchemaName($"{Ns}.{id}") };

    private static SchemaGroup Root(params string[] chain)
    {
        var field = new SchemaField
        {
            Name = "A",
            DataType = "sys.int32",
            ProcessorsList = [.. chain.Select(Ref)]
        };
        var group = new SchemaGroup { Name = "Root" };
        group.MemberList.Add(field);
        return group;
    }

    private static byte[] Write(ExecutionPlan plan, int value)
    {
        var output = new ArrayBufferWriter<byte>();
        var result = new WriterSession(plan, output).Write(new DictSource(new() { ["Root.A"] = value }));
        Assert.That(result, Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    private static object? Read(ExecutionPlan plan, byte[] bytes)
    {
        var sink = new DictSink();
        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);
        Assert.That(result, Is.EqualTo(ReadResult.Success));
        return sink.Values["Root.A"];
    }

    [Test]
    public void Chain_TransformsEncodedValue_RoundTrip()
    {
        var plan = Build(Root("head", "invert"), []);

        var bytes = Write(plan, 0x01020304);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xFB, 0xFC, 0xFD, 0xFE }));
        Assert.That(Read(plan, bytes), Is.EqualTo(0x01020304));
    }

    [Test]
    public void Chain_TwoChainedStages_RunInOrderOnWrite_ReverseOnRead()
    {
        var log = new List<string>();
        var plan = Build(Root("head", "invert", "tag"), log);

        var bytes = Write(plan, 1);
        Read(plan, bytes);

        Assert.That(log, Is.EqualTo(new[]
        {
            "Write:head", "Write:invert", "Write:tag",
            "Read:tag", "Read:invert", "Read:head"
        }));
    }

    [Test]
    public void Chain_ProcessorWithoutChainSupport_FailsThePlan()
    {
        var ex = Assert.Throws<ExecutionPlanException>(() => Build(Root("head", "plainhead"), []));

        Assert.That(ex!.Message, Does.Contain("fc.plainhead").And.Contain("field chain"));
    }

    private sealed class Factory(List<string> log) : IProcessorFactory
    {
        public string Namespace => Ns;

        public IProcessor? CreateProcessor(string id) => id switch
        {
            "head" => new Head(log, "head"),
            "plainhead" => new Head(log, "plainhead"),
            "invert" => new Invert(log, "invert", true),
            "tag" => new Invert(log, "tag", false),
            _ => null
        };
    }

    /// <summary>A chain head: int to 4 little-endian bytes. Only implements the standard field interface.</summary>
    private sealed class Head(List<string> log, string id) : IFieldProcessor
    {
        public ProcessorKey Key => new(Ns, id);
        public string Name => id;
        public PipelineStage Stage => PipelineStage.Representation;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
        {
            log.Add($"Write:{id}");
            var bytes = BitConverter.GetBytes((int)field.Value!);
            return FieldWriteResult<EncodedField>.Written(new EncodedField(field.Name, typeof(byte[]), bytes, 32), 32);
        }

        public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
        {
            log.Add($"Read:{id}");
            return FieldReadResult<LogicalField>.Consumed(
                new LogicalField(field.Name, typeof(int), BitConverter.ToInt32((byte[])field.Value!)), 32);
        }
    }

    /// <summary>A chained stage (EncodedField to EncodedField); optionally inverts the bytes (it is its own inverse).</summary>
    private sealed class Invert(List<string> log, string id, bool invert) :
        IFieldProcessor,
        IFieldWriter<EncodedField, EncodedField>,
        IFieldReader<EncodedField, EncodedField>
    {
        public ProcessorKey Key => new(Ns, id);
        public string Name => id;
        public PipelineStage Stage => PipelineStage.Representation;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
            => throw new NotSupportedException("Only valid after a head.");

        public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
            => throw new NotSupportedException("Only valid after a head.");

        public FieldWriteResult<EncodedField> Write(EncodedField field, FieldProcessorContext context)
        {
            log.Add($"Write:{id}");
            return FieldWriteResult<EncodedField>.Written(Transform(field), field.BitWidth);
        }

        FieldReadResult<EncodedField> IFieldReader<EncodedField, EncodedField>.Read(EncodedField field, FieldProcessorContext context)
        {
            log.Add($"Read:{id}");
            return FieldReadResult<EncodedField>.Consumed(Transform(field), field.BitWidth);
        }

        private EncodedField Transform(EncodedField field)
        {
            if (!invert)
            {
                return field;
            }

            var bytes = ((byte[])field.Value!).Select(b => (byte)~b).ToArray();
            return field with { Value = bytes };
        }
    }
}
