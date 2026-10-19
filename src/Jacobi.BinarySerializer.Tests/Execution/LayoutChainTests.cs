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
        => new() { Processor = new SchemaName($"{Ns}.{id}") };

    private static SchemaField Field(string name, SchemaName? type = null)
        => new() { Name = name, DataType = type ?? "sys.uint8" };

    private static SchemaGroup Group(string name, SchemaProcessorRef[] processors, params SchemaNode[] members)
    {
        var group = new SchemaGroup { Name = name, ProcessorsList = [.. processors] };
        group.MemberList.AddRange(members);
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
    public void Chain_BeginRunsOuterToInner_EndRunsInnerToOuter()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("align")], Field("A")), log);

        Write(plan, new() { ["Root.A"] = (byte)1 });

        var lifecycle = log.Where(l => l.StartsWith("Begin") || l.StartsWith("End")).ToArray();
        Assert.That(lifecycle, Is.EqualTo(new[] { "Begin:head", "Begin:align", "End:align", "End:head" }));
    }

    [Test]
    public void Chain_InnerGroupOutputFlowsThroughOuterStages()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("trailhead"), Ref("prefixhead")], Field("A")), log);

        var bytes = Write(plan, new() { ["Root.A"] = (byte)1 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAA, 1, 0xEE }));
        Assert.That(log.Where(l => l.StartsWith("Begin") || l.StartsWith("End")),
            Is.EqualTo(new[] { "Begin:trailhead", "Begin:prefixhead", "End:prefixhead", "End:trailhead" }));
    }

    [Test]
    public void Chain_NestedGroupPositionIsRelativeToOuterProcessorOutput()
    {
        var log = new List<string>();
        var plan = Build(
            Group("Root", [Ref("prefixhead")], Group("G", [Ref("align")], Field("X"))), log);

        var bytes = Write(plan, new() { ["Root.G.X"] = (byte)9 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAA, 9, 0, 0, 0 }));
        Assert.That(Read(plan, bytes), Is.EqualTo(new Dictionary<string, object?> { ["Root.G.X"] = (byte)9 }));
    }

    [Test]
    public void Chain_ContextReportsRootAndGroupPosition()
    {
        var log = new List<string>();
        var plan = Build(
            Group("Root", [], Field("P", "sys.uint16"), Group("G", [Ref("head"), Ref("align")], Field("X"))), log);

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
    public void Chain_Write_ReportsRepeatIndexAndCount()
    {
        var log = new List<string>();
        var repeat = new SchemaRepeat { Name = "Items", Count = 2, ProcessorsList = [Ref("probe")] };
        repeat.MemberList.Add(Field("V"));
        var plan = Build(Group("Root", [], repeat), log);

        var output = new ArrayBufferWriter<byte>();
        var result = new WriterSession(plan, output).Write(new ConstSource());

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(log, Does.Contain("BeginW:r0/2"));
        Assert.That(log, Does.Contain("BeginW:r1/2"));
    }

    [Test]
    public void Chain_Read_ReportsChoiceIndex()
    {
        var log = new List<string>();
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = 1, ProcessorsList = [Ref("probe")] };
        choice.MemberList.Add(Field("A"));
        choice.MemberList.Add(Field("B"));
        var plan = Build(Group("Root", [], choice), log);

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 5 }), new NullSink());

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(log, Does.Contain("BeginR:c1"));
    }

    [Test]
    public void Chain_ProcessorWithoutChainSupport_FailsThePlan()
    {
        var log = new List<string>();

        var ex = Assert.Throws<ExecutionPlanException>(
            () => Build(Group("Root", [Ref("plainhead"), Ref("head")], Field("A")), log));

        Assert.That(ex!.Message, Does.Contain("ch.plainhead").And.Contain("chained"));
    }

    private sealed class Factory(List<string> log) : IProcessorFactory
    {
        public string Namespace => Ns;

        public IProcessor? CreateProcessor(string id) => id switch
        {
            "head" => new Head(log, "head", false),
            "trailhead" => new Head(log, "trailhead", true),
            "prefixhead" => new Head(log, "prefixhead", false, true),
            "plainhead" => new PlainHead(),
            "align" => new Align(log),
            "probe" => new Probe(log),
            _ => null
        };
    }

    /// <summary>A layout processor that can act as the innermost field codec or an outer byte wrapper.</summary>
    private sealed class Head(List<string> log, string id, bool trailer, bool prefix = false) : ILayoutProcessor, ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
    {
        public ProcessorKey Key => new(Ns, id);
        public string Name => id;
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        {
            log.Add($"Begin:{id}");
            if (prefix)
            {
                writer.GetSpan(1)[0] = 0xAA;
                writer.Advance(1);
            }
        }

        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);

        public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
        {
            log.Add($"Write:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            return LayoutChain.PassThroughWrite(writer, bytes);
        }

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
        {
            log.Add($"Begin:{id}");
            if (prefix && (!reader.TryRead(out var value) || value != 0xAA))
            {
                throw new InvalidOperationException("The outer layout prefix is missing.");
            }
        }

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            return DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            return LayoutChain.PassThroughRead(ref reader);
        }

        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add($"End:{id}");
    }

    private sealed class PlainHead : ILayoutProcessor
    {
        public ProcessorKey Key => new(Ns, "plainhead");
        public string Name => "plainhead";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);
        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
    }

    private sealed class ConstSource : IFieldSource
    {
        public SourceResult GetField(FieldContext context)
            => SourceResult.Provided(new LogicalField(context.Name, typeof(byte), (byte)1));
    }

    private sealed class NullSink : IFieldSink
    {
        public void SetField(FieldContext context, LogicalField value) { }
    }

    private sealed class Probe(List<string> log) : ILayoutProcessor
    {
        public ProcessorKey Key => new(Ns, "probe");
        public string Name => "probe";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        private static string Describe(LayoutProcessorContext context)
            => context.GroupData.ChoiceIndex is { } c ? $"c{c}" : $"r{context.GroupData.RepeatIndex}/{context.GroupData.RepeatCount}";

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
            => log.Add($"BeginW:{Describe(context)}");

        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);

        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        {
        }

        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => log.Add($"BeginR:{Describe(context)}");

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Read(ref reader, context);

        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
        }
    }

    /// <summary>Pads to a multiple of 4 (relative to the owning group) before each value and at the end of the group.</summary>
    private sealed class Align(List<string> log) : ILayoutProcessor, ILayoutWriter<ReadOnlySpan<byte>>, ILayoutReader<ReadOnlyMemory<byte>>
    {
        public ProcessorKey Key => new(Ns, "align");
        public string Name => "align";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        private static int Padding(LayoutProcessorContext context) => (int)((4 - context.GroupData.Position % 4) % 4);

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
        {
            Pad(writer, Padding(context));
            return DefaultProcessors.LayoutProcessor.Write(writer, encodedValue, context);
        }

        public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
        {
            log.Add($"Write:{context.GroupData.RootPosition}/{context.GroupData.Position}");
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
        {
            if (!TrySkip(ref reader, context))
            {
                return LayoutReadResult<EncodedField>.NeedMoreData();
            }
            return DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            if (!TrySkip(ref reader, context))
            {
                return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData();
            }
            return LayoutReadResult<ReadOnlyMemory<byte>>.Success(LayoutChain.Unread(reader));
        }

        private static bool TrySkip(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var padding = Padding(context);
            if (reader.Remaining < padding)
            {
                return false;
            }
            reader.Advance(padding);
            return true;
        }

        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add("End:align");
            var padding = Padding(context);
            reader.Advance(Math.Min(padding, reader.Remaining));
        }
    }
}
