using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class LayoutWindowSessionTests
{
    private static SchemaProcessorRef Custom(string id) => new() { Processor = new SchemaName($"window.{id}") };

    private static ExecutionPlan Plan(SchemaGroup root, params IProcessor[] processors)
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory(processors));
        manager.Register(new ProcessorFactory());
        var dataTypes = Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        return new ExecutionPlanBuilder(manager, dataTypes).Build(root);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Read_NeedMoreData_RefillsAfterNonemptyInputWithoutConsumingTheFollowingField(bool counted)
    {
        var windows = new List<long>();
        var root = Group("Root", [], Field("Value", "sys.uint8", [Custom("wide")]), Field("After", "sys.uint8"));
        var plan = Plan(root, new WideLayout(windows, counted));
        var bytes = Enumerable.Range(0, 25).Select(value => (byte)value).Append((byte)99).ToArray();
        var sink = new DictSink();
        var session = new ReaderSession(plan);

        Assert.That(session.Read(new ReadOnlySequence<byte>(bytes), sink), Is.EqualTo(ReadResult.Success));

        Assert.That(sink.Values["Root.Value"], Is.EqualTo((byte)24));
        Assert.That(sink.Values["Root.After"], Is.EqualTo((byte)99));
        Assert.That(session.BytesRead, Is.EqualTo(26));
        Assert.That(windows, Is.EqualTo(counted ? new long[] { 0, 25 } : new long[] { 0, 10, 20, 26 }));
    }

    [Test]
    public void Read_SuccessWithoutEncodedField_ReportsAnExplicitError()
    {
        var plan = Plan(Group("Root", [Custom("empty")], Field("Value", "sys.uint8")), new EmptyLayout());
        var sink = new DictSink();
        var reader = new ReaderSession(plan);

        Assert.That(() => reader.Read(ReadOnlySequence<byte>.Empty, sink),
            Throws.InvalidOperationException.With.Message.Contains("Root.Value").And.Message.Contains("without an encoded field"));
        Assert.That(sink.Values, Is.Empty);
    }

    [Test]
    public void Read_UncountedWindowThroughParent_AcceptsAShortFinalRefill()
    {
        var windows = new List<long>();
        var plan = Plan(Group("Root", [Custom("xor"), Custom("wide")], Field("Value", "sys.uint8")),
            new XorLayout(), new WideLayout(windows, false));
        var bytes = Enumerable.Range(0, 25).Select(value => (byte)(value ^ 0xFF)).ToArray();
        var sink = new DictSink();
        var reader = new ReaderSession(plan);

        Assert.That(reader.Read(new ReadOnlySequence<byte>(bytes), sink), Is.EqualTo(ReadResult.Success));

        Assert.That(sink.Values["Root.Value"], Is.EqualTo((byte)24));
        Assert.That(windows, Is.EqualTo(new long[] { 0, 10, 20, 25 }));
        Assert.That(reader.BytesRead, Is.EqualTo(25));
    }

    [Test]
    public void Read_TruncatedWindow_ReturnsNeedMoreDataWithoutPublishingAField()
    {
        var windows = new List<long>();
        var plan = Plan(Group("Root", [], Field("Value", "sys.uint8", [Custom("wide")])), new WideLayout(windows, false));
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[21]), sink);

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(windows, Is.EqualTo(new long[] { 0, 10, 20, 21 }));
        Assert.That(sink.Values, Is.Empty);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void Read_HooksReceiveEnclosingStageOutputAndReturnNeedMoreDataWhenTruncated(int length)
    {
        var root = Group("Root", [Custom("xor"), Custom("frame")], Field("Value", "sys.uint8"));
        var plan = Plan(root, new XorLayout(), new FramedLayout());
        byte[] wire = [0x54, 0xF8, 0x32];
        var sink = new DictSink();
        var session = new ReaderSession(plan);

        var result = session.Read(new ReadOnlySequence<byte>(wire.AsMemory(0, length)), sink);

        Assert.That(result, Is.EqualTo(length == 3 ? ReadResult.Success : ReadResult.NeedMoreData));
        Assert.That(sink.Values, Is.EqualTo(length >= 2
            ? new Dictionary<string, object?> { ["Root.Value"] = (byte)7 }
            : new Dictionary<string, object?>()));
        if (length == 3)
        {
            Assert.That(session.BytesRead, Is.EqualTo(3));
        }
    }

    [Test]
    public void RoundTrip_OpenWidthFieldAcrossRefills_AlignsOnceAndPreservesCrcAndNextField()
    {
        var root = Group("Root", [Ref("crc"), Ref("align", ("bytes", "4"))],
            Field("Value", "sys.uint32", [Ref("varint")]), Field("After", "sys.uint8"));
        var plan = Plan(root);
        var values = new Dictionary<string, object?> { ["Root.Value"] = 300U, ["Root.After"] = (byte)9 };
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(new DictSource(values)), Is.EqualTo(WriteResult.Success));
        var sink = new DictSink();
        var reader = new ReaderSession(plan);

        Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));

        Assert.That(output.WrittenSpan[..8].ToArray(), Is.EqualTo(new byte[] { 0xAC, 2, 0, 0, 9, 0, 0, 0 }));
        Assert.That(sink.Values, Is.EqualTo(values));
        Assert.That(reader.BytesRead, Is.EqualTo(output.WrittenCount));
    }

    [Test]
    public void RoundTrip_VirtualLookahead_DoesNotAdvanceStageWindowsOrCountCrcTwice()
    {
        var peek = new SchemaField { Name = "Peek", DataType = "sys.uint16", ByteOffset = 0 };
        var root = Group("Root", [Ref("crc"), Ref("bytepacker", ("byteorder", "big"))],
            peek, Field("Value", "sys.uint16"), Field("After", "sys.uint8"));
        var plan = Plan(root);
        var values = new Dictionary<string, object?>
        {
            ["Root.Peek"] = (ushort)0x1234,
            ["Root.Value"] = (ushort)0x1234,
            ["Root.After"] = (byte)9
        };
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(new DictSource(values)), Is.EqualTo(WriteResult.Success));
        var sink = new DictSink();
        var reader = new ReaderSession(plan);

        Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));

        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?>
        {
            ["Root.Peek"] = (ushort)0x1234,
            ["Root.Value"] = (ushort)0x1234,
            ["Root.After"] = (byte)9
        }));
        Assert.That(output.WrittenSpan[..3].ToArray(), Is.EqualTo(new byte[] { 0x12, 0x34, 9 }));
        Assert.That(reader.BytesRead, Is.EqualTo(output.WrittenCount));
    }

    [Test]
    public void Read_UnusedParentOutput_RemainsAvailableForTheNextField()
    {
        var calls = new List<int>();
        var plan = Plan(Group("Root", [Custom("burst"), Ref("bytepacker")],
            Field("A", "sys.uint8"), Field("B", "sys.uint8")), new BurstLayout(calls));
        var sink = new DictSink();
        var reader = new ReaderSession(plan);

        Assert.That(reader.Read(new ReadOnlySequence<byte>(new byte[] { 3, 7 }), sink), Is.EqualTo(ReadResult.Success));

        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?> { ["Root.A"] = (byte)3, ["Root.B"] = (byte)7 }));
        Assert.That(calls, Is.EqualTo(new[] { 2 }));
        Assert.That(reader.BytesRead, Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_CrcUsesItsOwnTransformedInputIncludingNestedScopeTrailers(bool nested)
    {
        SchemaNode child = nested ? Group("Child", [Ref("crc")], Field("Value", "sys.uint8")) : Field("Value", "sys.uint8");
        var root = Group("Root", [Custom("xor"), Ref("crc")], child);
        var plan = Plan(root, new XorLayout());
        var values = new Dictionary<string, object?> { [nested ? "Root.Child.Value" : "Root.Value"] = (byte)7 };
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(new DictSource(values)), Is.EqualTo(WriteResult.Success));
        var sink = new DictSink();
        var reader = new ReaderSession(plan);

        Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));

        Assert.That(sink.Values, Is.EqualTo(values));
        Assert.That(output.WrittenSpan[0], Is.EqualTo((byte)0xF8));
        Assert.That(output.WrittenCount, Is.EqualTo(nested ? 9 : 5));
        var corrupted = output.WrittenSpan.ToArray();
        corrupted[0] ^= 1;
        Assert.That(() => reader.Read(new ReadOnlySequence<byte>(corrupted), new DictSink()),
            Throws.InvalidOperationException.With.Message.Contains("CRC mismatch"));
    }

    private sealed class Factory(IProcessor[] processors) : IProcessorFactory
    {
        public string Namespace => "window";
        public IProcessor? CreateProcessor(string id) => processors.SingleOrDefault(processor => processor.Key.Id == id);
    }

    private abstract class Layout(string id) : ILayoutProcessor
    {
        public ProcessorKey Key => new("window", id);
        public string Name => id;
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public virtual void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public virtual WriteResult Write(IBufferWriter<byte> writer, EncodedField value, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Write(writer, value, context);
        public virtual void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public virtual LayoutReadResult BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) => LayoutReadResult.Success();
        public virtual LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => DefaultProcessors.LayoutProcessor.Read(ref reader, context);
        public virtual LayoutReadResult EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) => LayoutReadResult.Success();
    }

    private sealed class EmptyLayout() : Layout("empty")
    {
        public override LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => LayoutReadResult<EncodedField>.Success(default!);
    }

    private sealed class WideLayout(List<long> windows, bool counted) : Layout("wide")
    {
        public override LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            windows.Add(reader.Remaining);
            if (reader.Remaining < 25)
            {
                reader.Advance(reader.Remaining);
                return LayoutReadResult<EncodedField>.NeedMoreData(counted ? 25 : null);
            }
            reader.Advance(24);
            reader.TryRead(out var value);
            return LayoutReadResult<EncodedField>.Success(new(context.Field!.Name, typeof(byte[]), new byte[] { value }, 8));
        }
    }

    private sealed class XorLayout() : Layout("xor"), ILayoutReader<ReadOnlyMemory<byte>>, ILayoutWriter<ReadOnlySpan<byte>>
    {
        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = LayoutChain.PassThroughRead(ref reader, context.InputByteLength);
            return result.Status == ReadResult.Success
                ? LayoutReadResult<ReadOnlyMemory<byte>>.Success(result.Value.ToArray().Select(value => (byte)(value ^ 0xFF)).ToArray())
                : result;
        }
        public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> value, LayoutProcessorContext context)
        {
            foreach (var item in value)
            {
                writer.GetSpan(1)[0] = (byte)(item ^ 0xFF);
                writer.Advance(1);
            }
            return WriteResult.Success;
        }
    }

    private sealed class BurstLayout(List<int> calls) : Layout("burst"), ILayoutReader<ReadOnlyMemory<byte>>, ILayoutWriter<ReadOnlySpan<byte>>
    {
        LayoutReadResult<ReadOnlyMemory<byte>> ILayoutReader<ReadOnlyMemory<byte>>.Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
        {
            var result = LayoutChain.PassThroughRead(ref reader, 2);
            if (result.Status == ReadResult.Success)
            {
                calls.Add(result.Value.Length);
            }
            return result;
        }
        public WriteResult Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes, LayoutProcessorContext context)
            => LayoutChain.PassThroughWrite(writer, bytes);
    }

    private sealed class FramedLayout() : Layout("frame")
    {
        public override LayoutReadResult BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) => ReadMarker(ref reader, 0xAB);
        public override LayoutReadResult EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) => ReadMarker(ref reader, 0xCD);
        private static LayoutReadResult ReadMarker(ref SequenceReader<byte> reader, byte expected)
        {
            if (!reader.TryRead(out var value))
            {
                return LayoutReadResult.NeedMoreData(1);
            }
            Assert.That(value, Is.EqualTo(expected));
            return LayoutReadResult.Success();
        }
    }
}
