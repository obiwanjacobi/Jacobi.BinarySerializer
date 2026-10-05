using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class LayoutChainTests
{
    private const string Ns = "ch";

    private static ExecutionPlan Build(SchemaGroup root, List<string> log)
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory(log));
        return new ExecutionPlanBuilder(manager).Build(root);
    }

    private static SchemaProcessorRef Ref(string id)
        => new() { Processor = new SchemaProcessorName($"{Ns}.{id}") };

    private static SchemaField Field(string name, SchemaDataType type = SchemaDataType.UInt8)
        => new() { Name = name, Type = type };

    private static SchemaGroup Group(string name, SchemaProcessorRef[] processors, params SchemaNode[] children)
    {
        var group = new SchemaGroup { Name = name, ProcessorsList = [.. processors] };
        group.ChildList.AddRange(children);
        return group;
    }

    private static byte[] Write(ExecutionPlan plan, Dictionary<string, object?> values)
    {
        var output = new ArrayBufferWriter<byte>();
        var result = new WriterSession(plan, output).Write(new DictSource(values));
        Assert.That(result, Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    private static Dictionary<string, object?> Read(ExecutionPlan plan, byte[] bytes)
    {
        var sink = new DictSink();
        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);
        Assert.That(result, Is.EqualTo(ReadResult.Success));
        return sink.Values;
    }

    [Test]
    public void Chain_AlignsFieldsAndGroupEnd_RoundTrip()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("align")], Field("A"), Field("B")), log);
        var values = new Dictionary<string, object?> { ["Root.A"] = (byte)1, ["Root.B"] = (byte)2 };

        var bytes = Write(plan, values);

        // A at 0; B padded from 1 to 4; end of group padded from 5 to 8.
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
        Assert.That(Read(plan, bytes), Is.EqualTo(values));
    }

    [Test]
    public void Chain_BeginRunsLastToFirst_EndRunsFirstToLast()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("align")], Field("A")), log);

        Write(plan, new() { ["Root.A"] = (byte)1 });

        var lifecycle = log.Where(l => l.StartsWith("Begin") || l.StartsWith("End")).ToArray();
        Assert.That(lifecycle, Is.EqualTo(new[] { "Begin:align", "Begin:head", "End:head", "End:align" }));
    }

    [Test]
    public void Chain_HeadGroupOutputFlowsThroughLaterStages()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("trailhead"), Ref("align")], Field("A")), log);

        var bytes = Write(plan, new() { ["Root.A"] = (byte)1 });

        // the head emits 0xEE in EndWrite; it passes through the aligner (pad 1 -> 4), then the aligner pads the end (5 -> 8).
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0, 0, 0xEE, 0, 0, 0 }));
    }

    [Test]
    public void Chain_ContextReportsRootAndGroupPosition()
    {
        var log = new List<string>();
        var plan = Build(
            Group("Root", [], Field("P", SchemaDataType.UInt16), Group("G", [Ref("head"), Ref("align")], Field("X"))), log);

        var bytes = Write(plan, new() { ["Root.P"] = (ushort)0x0102, ["Root.G.X"] = (byte)9 });

        Assert.That(log, Does.Contain("Write:2/0"));
        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 1, 9, 0, 0, 0 }));
    }

    [Test]
    public void Chain_Read_ReportsPositionOfTheField()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("align")], Field("A"), Field("B")), log);

        Read(plan, [1, 0, 0, 0, 2, 0, 0, 0]);

        Assert.That(log, Does.Contain("Read:0/0"));
        Assert.That(log, Does.Contain("Read:1/1"));
    }

    [Test]
    public void Chain_ProcessorWithoutChainSupport_FailsThePlan()
    {
        var log = new List<string>();

        var ex = Assert.Throws<ExecutionPlanException>(
            () => Build(Group("Root", [Ref("head"), Ref("plainhead")], Field("A")), log));

        Assert.That(ex!.Message, Does.Contain("ch.plainhead").And.Contain("chained"));
    }

    private sealed class Factory(List<string> log) : IProcessorFactory
    {
        public string Namespace => Ns;

        public IProcessor? CreateProcessor(string id) => id switch
        {
            "head" => new Head(log, "head", false),
            "trailhead" => new Head(log, "trailhead", true),
            "plainhead" => new Head(log, "plainhead", false),
            "align" => new Align(log),
            _ => null
        };
    }

    /// <summary>A chain head that only implements the standard layout interface.</summary>
    private sealed class Head(List<string> log, string id, bool trailer) : ILayoutProcessor
    {
        public ProcessorKey Key => new(Ns, id);
        public string Name => id;
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
            => log.Add($"Begin:{id}");

        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
            => ProcessorDefaults.DefaultLayoutProcessor.Write(writer, encodedValue, context);

        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        {
            log.Add($"End:{id}");
            if (trailer)
            {
                writer.GetSpan(1)[0] = 0xEE;
                writer.Advance(1);
            }
        }

        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add($"Begin:{id}");

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.RootPosition}/{context.GroupPosition}");
            return ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, context);
        }

        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add($"End:{id}");
    }

    /// <summary>Pads to a multiple of 4 (relative to the owning group) before each value and at the end of the group.</summary>
    private sealed class Align(List<string> log) : ILayoutProcessor, ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
    {
        public ProcessorKey Key => new(Ns, "align");
        public string Name => "align";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        private static int Padding(LayoutProcessorContext context) => (int)((4 - context.GroupPosition % 4) % 4);

        private static void Pad(IBufferWriter<byte> writer, int count)
        {
            if (count > 0)
            {
                writer.GetSpan(count).Slice(0, count).Clear();
                writer.Advance(count);
            }
        }

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
            => log.Add("Begin:align");

        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
            => ProcessorDefaults.DefaultLayoutProcessor.Write(writer, encodedValue, context);

        public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
        {
            log.Add($"Write:{context.RootPosition}/{context.GroupPosition}");
            Pad(writer, Padding(context));
            writer.Write(bytes);
            return WriteResult.Success;
        }

        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        {
            log.Add("End:align");
            Pad(writer, Padding(context));
        }

        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add("Begin:align");

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => ProcessorDefaults.DefaultLayoutProcessor.Read(ref reader, context);

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.RootPosition}/{context.GroupPosition}");
            var padding = Padding(context);
            if (reader.Remaining < padding)
            {
                return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData();
            }
            reader.Advance(padding);
            return LayoutReadResult<ReadOnlyMemory<byte>>.Success(LayoutChain.Unread(reader));
        }

        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add("End:align");
            var padding = Padding(context);
            reader.Advance(Math.Min(padding, reader.Remaining));
        }
    }
}
