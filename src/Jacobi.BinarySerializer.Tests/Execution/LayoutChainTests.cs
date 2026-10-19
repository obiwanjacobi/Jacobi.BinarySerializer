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

    [Test]
    public void Chain_BufferedWrite_DoesNotCallOuterWrapperUntilBytesAreEmitted()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("buffered")], Field("A"), Field("B")), log);

        var bytes = Write(plan, new() { ["Root.A"] = (byte)1, ["Root.B"] = (byte)2 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(log, Is.EqualTo(new[]
        {
            "Begin:head", "Begin:buffered", "Buffer:A", "Buffer:B",
            "End:buffered", "Write:0/0", "End:head"
        }));
    }

    [Test]
    public void Chain_BufferedRead_CallsOuterWrapperOnlyForNewInput()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("buffered")],
            Field("A"), Field("B"), Field("C"), Field("D")), log);

        var values = Read(plan, [1, 2, 3, 4]);

        Assert.That(values, Is.EqualTo(new Dictionary<string, object?>
        {
            ["Root.A"] = (byte)1, ["Root.B"] = (byte)2,
            ["Root.C"] = (byte)3, ["Root.D"] = (byte)4
        }));
        Assert.That(log, Is.EqualTo(new[] { "Begin:head", "Read:0/0", "Read:2/2", "End:head" }));
    }

    [Test]
    public void Chain_BufferedRead_TruncatedInputNeedsMoreDataWithoutSuccessfulTransfer()
    {
        var log = new List<string>();
        var plan = Build(Group("Root", [Ref("head"), Ref("buffered")], Field("A")), log);
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 1 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(sink.Values, Is.Empty);
        Assert.That(log, Is.EqualTo(new[] { "Begin:head" }));
    }

    [Test]
    public void Wrapper_Read_ConsumesOnlyRequestedOperationBytes()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }));

        var result = LayoutChain.PassThroughRead(ref reader, 2);

        Assert.That(result.Status, Is.EqualTo(ReadResult.Success));
        Assert.That(result.Value.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(reader.Consumed, Is.EqualTo(2));
        Assert.That(reader.Remaining, Is.EqualTo(1));
    }

    [Test]
    public void Wrapper_Read_TruncatedOperationDoesNotConsumeInput()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 1 }));

        var result = LayoutChain.PassThroughRead(ref reader, 2);

        Assert.That(result.Status, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(result.RequestedByteCount, Is.EqualTo(2));
        Assert.That(reader.Consumed, Is.Zero);
    }

    [Test]
    public void AlignWrapper_Read_ContinuationDoesNotPadWithinTheField()
    {
        var context = new LayoutProcessorContext(new ReaderSession(Build(Group("Root", [], Field("A")), [])))
        {
            Services = null!,
            InputByteLength = 1,
            IsReadContinuation = true
        };
        context.GroupData.Position = 1;
        ILayoutReader<ReadOnlyMemory<byte>> align = new Align([]);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[] { 0x81, 0x01 }));

        var result = align.Read(ref reader, context);

        Assert.That(result.Status, Is.EqualTo(ReadResult.Success));
        Assert.That(result.Value.ToArray(), Is.EqualTo(new byte[] { 0x81 }));
        Assert.That(reader.Consumed, Is.EqualTo(1));
        Assert.That(reader.Remaining, Is.EqualTo(1));
    }

    [TestCase(0)]
    [TestCase(2)]
    public void AlignWrapper_Read_RequestsPaddingAndExactOutputLength(int available)
    {
        var context = new LayoutProcessorContext(new ReaderSession(Build(Group("Root", [], Field("A")), [])))
        {
            Services = null!,
            InputByteLength = 2
        };
        context.GroupData.Position = 3;
        ILayoutReader<ReadOnlyMemory<byte>> align = new Align([]);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(new byte[available]));

        var result = align.Read(ref reader, context);

        Assert.That(result.Status, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(result.RequestedByteCount, Is.EqualTo(3));
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
            "buffered" => new BufferedLayout(log),
            _ => null
        };
    }

    private sealed class BufferedLayout(List<string> log) : ILayoutProcessor
    {
        public ProcessorKey Key => new(Ns, "buffered");
        public string Name => "buffered";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        {
            Assert.That(context.Field, Is.Null);
            Assert.That(context.FieldData.ByteLength, Is.Null);
            context.GetOrCreateState<ArrayBufferWriter<byte>>().Clear();
            log.Add("Begin:buffered");
        }

        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
        {
            log.Add($"Buffer:{context.Field!.Name}");
            return DefaultProcessors.LayoutProcessor.Write(
                context.GetOrCreateState<ArrayBufferWriter<byte>>(), encodedValue, context);
        }

        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context)
        {
            Assert.That(context.Field, Is.Null);
            Assert.That(context.FieldData.ByteLength, Is.Null);
            log.Add("End:buffered");
            writer.Write(context.GetOrCreateState<ArrayBufferWriter<byte>>().WrittenSpan);
        }

        public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            context.GetOrCreateState<Queue<byte>>().Clear();
            context.HasBufferedInput = false;
            return LayoutReadResult<EncodedField>.Success();
        }

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var queue = context.GetOrCreateState<Queue<byte>>();
            if (queue.Count == 0)
            {
                if (reader.Remaining < 2)
                {
                    return LayoutReadResult<EncodedField>.NeedMoreData(2);
                }
                reader.TryRead(out var first);
                reader.TryRead(out var second);
                queue.Enqueue(first);
                queue.Enqueue(second);
            }
            var value = queue.Dequeue();
            context.HasBufferedInput = queue.Count > 0;
            return LayoutReadResult<EncodedField>.Success(new(context.Field!.Name, typeof(byte[]), new byte[] { value }, 8));
        }

        public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            context.GetOrCreateState<Queue<byte>>().Clear();
            context.HasBufferedInput = false;
            return LayoutReadResult<EncodedField>.Success();
        }
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

        public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            if (prefix)
            {
                if (!reader.TryRead(out var value))
                {
                    return LayoutReadResult<EncodedField>.NeedMoreData(1);
                }
                if (value != 0xAA)
                {
                    throw new InvalidOperationException("The outer layout prefix is missing.");
                }
            }
            log.Add($"Begin:{id}");
            return LayoutReadResult<EncodedField>.Success();
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = BeginRead(ref reader, context);
            return new(result.Status, RequestedByteCount: result.RequestedByteCount);
        }

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"Read:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            return DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = LayoutChain.PassThroughRead(ref reader, context.InputByteLength);
            if (result.Status == ReadResult.Success)
            {
                log.Add($"Read:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            }
            return result;
        }

        public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"End:{id}");
            return LayoutReadResult<EncodedField>.Success();
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = EndRead(ref reader, context);
            return new(result.Status, RequestedByteCount: result.RequestedByteCount);
        }
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
        public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => LayoutReadResult<EncodedField>.Success();
        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => LayoutReadResult<EncodedField>.Success();
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

        public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add($"BeginR:{Describe(context)}");
            return LayoutReadResult<EncodedField>.Success();
        }

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Read(ref reader, context);

        public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => LayoutReadResult<EncodedField>.Success();
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

        public LayoutReadResult<EncodedField> BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            log.Add("Begin:align");
            return LayoutReadResult<EncodedField>.Success();
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = BeginRead(ref reader, context);
            return new(result.Status, RequestedByteCount: result.RequestedByteCount);
        }

        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var padding = ReadPadding(context);
            if (!TrySkip(ref reader, context))
            {
                var length = context.FieldData.ByteLength
                    ?? (context.Field?.Pipeline.FieldProcessors.Count > 0 ? context.InputByteLength : context.FixedByteLength)
                    ?? 1;
                return LayoutReadResult<EncodedField>.NeedMoreData(padding + length);
            }
            var result = DefaultProcessors.LayoutProcessor.Read(ref reader, context);
            return result.Status == ReadResult.NeedMoreData
                ? LayoutReadResult<EncodedField>.NeedMoreData(padding + (result.RequestedByteCount ?? 1))
                : result;
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var padding = ReadPadding(context);
            if (!TrySkip(ref reader, context))
            {
                return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData(padding + (context.InputByteLength ?? 1));
            }
            var result = LayoutChain.PassThroughRead(ref reader, context.InputByteLength);
            if (result.Status == ReadResult.NeedMoreData)
            {
                return LayoutReadResult<ReadOnlyMemory<byte>>.NeedMoreData(padding + (result.RequestedByteCount ?? 1));
            }
            if (result.Status == ReadResult.Success)
            {
                log.Add($"Read:{context.GroupData.RootPosition}/{context.GroupData.Position}");
            }
            return result;
        }

        private static int ReadPadding(LayoutProcessorContext context)
            => context.IsReadContinuation ? 0 : Padding(context);

        private static bool TrySkip(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var padding = ReadPadding(context);
            if (reader.Remaining < padding)
            {
                return false;
            }
            reader.Advance(padding);
            return true;
        }

        public LayoutReadResult<EncodedField> EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var padding = ReadPadding(context);
            if (reader.Remaining < padding)
            {
                return LayoutReadResult<EncodedField>.NeedMoreData(padding);
            }
            log.Add("End:align");
            reader.Advance(padding);
            return LayoutReadResult<EncodedField>.Success();
        }

        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = EndRead(ref reader, context);
            return new(result.Status, RequestedByteCount: result.RequestedByteCount);
        }
    }
}
