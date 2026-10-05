using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class EngineEdgeCaseTests
{
    private const string Ns = "edge";

    private enum Mode { Ok, WriteFailure, ConsumeBits }

    private static ExecutionPlan BuildEdge(Mode mode, int bits = 0)
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory(mode, bits));
        var field = new SchemaField
        {
            Name = "A",
            Type = SchemaDataType.Int32,
            ProcessorsList = [new SchemaProcessorRef { Processor = new SchemaProcessorName($"{Ns}.proc") }]
        };
        var group = new SchemaGroup { Name = "Root" };
        group.ChildList.Add(field);
        return new ExecutionPlanBuilder(manager).Build(group);
    }

    [Test]
    public void Read_TruncatedVarInt_MidStream_ReturnsNeedMoreData()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.UInt32, [Ref("varint")]),
            Field("B", SchemaDataType.UInt8));

        // both bytes have the continuation bit set: the varint is cut off.
        var result = new ReaderSession(Build(root))
            .Read(new ReadOnlySequence<byte>(new byte[] { 0xAC, 0x82 }), new DictSink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_VarIntCompleteButFollowingFieldMissing_ReturnsNeedMoreData()
    {
        var root = Group("Root", [],
            Field("A", SchemaDataType.UInt32, [Ref("varint")]),
            Field("B", SchemaDataType.UInt8));

        var result = new ReaderSession(Build(root))
            .Read(new ReadOnlySequence<byte>(new byte[] { 0xAC, 0x02 }), new DictSink());

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [TestCase(7)]
    [TestCase(-8)]
    public void Read_ProcessorConsumesInvalidBits_Throws(int bits)
    {
        var plan = BuildEdge(Mode.ConsumeBits, bits);

        Assert.Throws<InvalidOperationException>(() =>
            new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }), new DictSink()));
    }

    [Test]
    public void Read_ProcessorConsumesMoreBitsThanProvided_Throws()
    {
        var plan = BuildEdge(Mode.ConsumeBits, 4096);

        Assert.Throws<InvalidOperationException>(() =>
            new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }), new DictSink()));
    }

    [Test]
    public void Write_FieldProcessorFailure_ReturnsFailure()
    {
        var plan = BuildEdge(Mode.WriteFailure);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(new DictSource(new() { ["Root.A"] = 1 }));

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    private sealed class Factory(Mode mode, int bits) : IProcessorFactory
    {
        public string Namespace => Ns;

        public IProcessor? CreateProcessor(string id) => id == "proc" ? new Proc(mode, bits) : null;
    }

    private sealed class Proc(Mode mode, int bits) : IFieldProcessor
    {
        public ProcessorKey Key => new(Ns, "proc");
        public string Name => "proc";
        public PipelineStage Stage => PipelineStage.Representation;
        public IReadOnlyList<PropertyDescriptor> Properties => [];

        public FieldWriteResult<EncodedField> Write(LogicalField field, FieldProcessorContext context)
        {
            if (mode == Mode.WriteFailure)
            {
                return FieldWriteResult<EncodedField>.Failure();
            }

            var bytes = BitConverter.GetBytes((int)field.Value!);
            return FieldWriteResult<EncodedField>.Written(new EncodedField(field.Name, typeof(byte[]), bytes, 32), 32);
        }

        public FieldReadResult<LogicalField> Read(EncodedField field, FieldProcessorContext context)
            => FieldReadResult<LogicalField>.Consumed(new LogicalField(field.Name, typeof(int), 0), bits);
    }
}
