using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class BitLayoutSessionTests
{
    private static SchemaGroup Flags(params SchemaProcessorRef[] wrappers)
        => Group("Flags", [.. wrappers, Ref("bitpacker")],
            Field("A", "sys.uint8", properties: [("bits", "4")]),
            Field("B", "sys.uint8", properties: [("bits", "4")]));

    private static byte[] Write(ExecutionPlan plan, Values values, PlanRange? range = null)
    {
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(values, range), Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    private static Values Read(ExecutionPlan plan, byte[] bytes, PlanRange? range = null)
    {
        var values = new Values();
        var session = new ReaderSession(plan);
        Assert.That(session.Read(new ReadOnlySequence<byte>(bytes), values, range), Is.EqualTo(ReadResult.Success));
        Assert.That(session.BytesRead, Is.EqualTo(bytes.Length));
        return values;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_OrdinaryScopes_AreVisitedPerField(bool nested)
    {
        var flags = Flags();
        var plan = Build(nested ? Group("Root", [], flags, Field("After", "sys.uint8")) : flags);
        var prefix = nested ? "Root.Flags" : "Flags";
        var values = new Values { Data = { [prefix + ".A"] = (byte)3, [prefix + ".B"] = (byte)12 } };
        if (nested)
        {
            values.Data["Root.After"] = (byte)9;
        }

        var bytes = Write(plan, values);
        var read = Read(plan, bytes);

        Assert.That(bytes, Is.EqualTo(nested ? new byte[] { 0xC3, 9 } : new byte[] { 0xC3 }));
        Assert.That(read.Data, Is.EqualTo(values.Data));
        Assert.That(values.Requests, Is.EqualTo(nested
            ? new[] { "Root.Flags.A", "Root.Flags.B", "Root.After" }
            : new[] { "Flags.A", "Flags.B" }));
    }

    [Test]
    public void RoundTrip_AlignmentAndCrc_WrapOnlyPhysicalBytes()
    {
        var plan = Build(Flags(Ref("crc"), Ref("align", ("bytes", "2"))));
        var values = new Values { Data = { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 } };

        var bytes = Write(plan, values);

        Assert.That(bytes, Has.Length.EqualTo(6));
        Assert.That(bytes.Take(2), Is.EqualTo(new byte[] { 0xC3, 0 }));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
        bytes[0] ^= 1;
        Assert.That(() => Read(plan, bytes), Throws.InvalidOperationException.With.Message.Contains("CRC mismatch"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_ExplicitRepresentationLength_DoesNotRequireExtraPhysicalBytes(bool align)
    {
        var wrappers = align ? new[] { Ref("align", ("bytes", "2")) } : Array.Empty<SchemaProcessorRef>();
        var field = new SchemaField
        {
            Name = "A", DataType = "sys.uint16", ByteLength = 2,
            PropertyList = [new SchemaProperty { Name = "sys.bitpacker.bits", Value = "4" }]
        };
        var plan = Build(Group("Flags", [.. wrappers, Ref("bitpacker")], field));
        var values = new Values { Data = { ["Flags.A"] = (ushort)3 } };

        var bytes = Write(plan, values);

        Assert.That(bytes, Is.EqualTo(align ? new byte[] { 3, 0 } : new byte[] { 3 }));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_CapacityPadding_PassesThroughOuterWrappers(bool align)
    {
        var wrappers = align ? new[] { Ref("crc"), Ref("align", ("bytes", "2")) } : new[] { Ref("crc") };
        var plan = Build(Group("Flags", [.. wrappers, Ref("bitpacker", ("byteLength", "3"))],
            Field("A", "sys.uint8", properties: [("bits", "3")])));
        var values = new Values { Data = { ["Flags.A"] = (byte)5 } };

        var bytes = Write(plan, values);

        Assert.That(bytes.Take(align ? 6 : 3), Is.EqualTo(align
            ? new byte[] { 5, 0, 0, 0, 0, 0 }
            : new byte[] { 5, 0, 0 }));
        Assert.That(bytes, Has.Length.EqualTo((align ? 6 : 3) + 4));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
    }

    [Test]
    public void RoundTrip_RedeclaredBitScope_StartsAtByteBoundaryAndAdvancesParentOffset()
    {
        var plan = Build(Group("Root", [Ref("bitpacker")],
            Field("A", "sys.uint8", properties: [("bits", "3")]),
            Group("Child", [Ref("bitpacker")], Field("C", "sys.uint8", properties: [("bits", "3")])),
            Field("B", "sys.uint8", properties: [("bits", "3"), ("bitoffset", "24")])));
        var values = new Values { Data = { ["Root.A"] = (byte)5, ["Root.Child.C"] = (byte)2, ["Root.B"] = (byte)7 } };

        var bytes = Write(plan, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 5, 2, 0, 7 }));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
    }

    [Test]
    public void Read_RedeclaredBitScope_DoesNotRestoreDiscardedParentPadding()
    {
        var repeat = new SchemaRepeat
        {
            Name = "Items", MemberList = [Field("Value", "sys.uint8", properties: [("bits", "4")])]
        };
        var plan = Build(Group("Root", [Ref("bitpacker")],
            Field("A", "sys.uint8", properties: [("bits", "3")]),
            Group("Child", [Ref("bitpacker")], Field("C", "sys.uint8", properties: [("bits", "3")])), repeat));
        var values = new Values { Count = -1, Data = { ["Root.A"] = (byte)5, ["Root.Child.C"] = (byte)2 } };

        var bytes = Write(plan, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 5, 2 }));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
    }

    [Test]
    public void RoundTrip_EmptyBitGroup_DoesNotEmitCapacityOrConsumeFollowingData()
    {
        var plan = Build(Group("Root", [], Group("Empty", [Ref("bitpacker", ("byteLength", "3"))]),
            Field("After", "sys.uint8")));
        var values = new Values { Data = { ["Root.After"] = (byte)9 } };

        var bytes = Write(plan, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 9 }));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
    }

    [Test]
    public void RoundTrip_ChildReference_IsPublishedBeforeNextField()
    {
        var flags = Flags();
        flags.MemberList[1] = new SchemaField
        {
            Name = "B", DataType = "sys.uint8", Value = new SchemaNodeRef { Path = "Flags.A" },
            PropertyList = [new SchemaProperty { Name = "sys.bitpacker.bits", Value = "4" }]
        };
        var plan = Build(flags);
        var values = new Values { Data = { ["Flags.A"] = (byte)3 } };

        var bytes = Write(plan, values);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x33 }));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
        Assert.That(values.Requests, Is.EqualTo(new[] { "Flags.A" }));
    }

    [Test]
    public void Range_CanSelectOneFieldOfBitLayoutGroup()
    {
        var plan = Build(Flags());
        var range = plan.CreateRange("Flags.B", "Flags.B");
        var values = new Values { Data = { ["Flags.B"] = (byte)12 } };

        var bytes = Write(plan, values, range);

        Assert.That(bytes, Is.EqualTo(new byte[] { 12 }));
        Assert.That(Read(plan, bytes, range).Data, Is.EqualTo(values.Data));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_RepeatScopes_ResetPackingState(bool open)
    {
        var repeat = new SchemaRepeat { Name = "Items", MemberList = [Flags()] };
        if (!open)
        {
            repeat = new SchemaRepeat { Name = "Items", Count = 2, MemberList = [Flags()] };
        }
        var plan = Build(Group("Root", [Ref("crc")], repeat));
        var values = new Values { Count = open ? -1 : 2, Data =
        {
            ["Root.Items.Flags.A[0]"] = (byte)3, ["Root.Items.Flags.B[0]"] = (byte)12,
            ["Root.Items.Flags.A[1]"] = (byte)4, ["Root.Items.Flags.B[1]"] = (byte)11
        } };

        var bytes = Write(plan, values);

        Assert.That(bytes.Take(2), Is.EqualTo(new byte[] { 0xC3, 0xB4 }));
        Assert.That(bytes, Has.Length.EqualTo(6));
        Assert.That(Read(plan, bytes).Data, Is.EqualTo(values.Data));
    }

    [Test]
    public void Read_OpenRepeat_TruncatedItemIsNotCleanEnd()
    {
        var flags = Flags();
        flags.MemberList[1] = Field("B", "sys.uint16", properties: [("bits", "12")]);
        var repeat = new SchemaRepeat { Name = "Items", MemberList = [flags] };
        var plan = Build(Group("Root", [], repeat));
        var sink = new Values();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(sink.Data, Is.EqualTo(new Dictionary<string, object?> { ["Root.Items.Flags.A[0]"] = (byte)3 }));
    }

    [Test]
    public void Read_CountlessRepeat_ConsumesBufferedInputBeforePhysicalEnd()
    {
        var repeat = new SchemaRepeat
        {
            Name = "Items", MemberList = [Field("Value", "sys.uint8", properties: [("bits", "4")])]
        };
        var plan = Build(Group("Root", [Ref("bitpacker")], repeat));

        var read = Read(plan, [0xC3]);

        Assert.That(read.Data, Is.EqualTo(new Dictionary<string, object?>
        {
            ["Root.Items.Value[0]"] = (byte)3, ["Root.Items.Value[1]"] = (byte)12
        }));
    }

    [Test]
    public void Read_SizedGroup_DoesNotConsumeFollowingField()
    {
        var flags = new SchemaGroup
        {
            Name = "Flags", ByteSize = 1, ProcessorsList = [Ref("bitpacker")],
            MemberList = [Field("A", "sys.uint16", properties: [("bits", "12")])]
        };
        var plan = Build(Group("Root", [], flags, Field("After", "sys.uint8")));
        var sink = new Values();

        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3, 0x99 }), sink),
            Throws.InvalidOperationException.With.Message.Contains("byte-size window"));
        Assert.That(sink.Data, Is.Empty);
    }

    [Test]
    public void RoundTrip_DeferredSize_UsesFlushedPhysicalBytes()
    {
        var flags = new SchemaGroup
        {
            Name = "Flags", ByteSize = new SchemaNodeRef { Path = "Root.Size" },
            ProcessorsList = [Ref("bitpacker")],
            MemberList = [Field("A", "sys.uint8", properties: [("bits", "3")])]
        };
        var plan = Build(Group("Root", [], Field("Size", "sys.uint8"), flags));
        var values = new Values { Data = { ["Root.Flags.A"] = (byte)5 } };

        var bytes = Write(plan, values);
        var read = Read(plan, bytes);

        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 5 }));
        Assert.That(read.Data["Root.Size"], Is.EqualTo((byte)1));
        Assert.That(read.Data["Root.Flags.A"], Is.EqualTo((byte)5));
    }

    [Test]
    public void Sessions_ReusedWithSharedPlan_DoNotRetainBufferedBits()
    {
        var plan = Build(Flags());
        var output = new ArrayBufferWriter<byte>();
        var writer = new WriterSession(plan, output);
        var reader = new ReaderSession(plan);
        for (byte value = 1; value <= 2; value++)
        {
            output.Clear();
            var values = new Values { Data = { ["Flags.A"] = value, ["Flags.B"] = (byte)12 } };
            Assert.That(writer.Write(values), Is.EqualTo(WriteResult.Success));
            Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { (byte)(0xC0 | value) }));
            var sink = new Values();
            Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));
            Assert.That(sink.Data, Is.EqualTo(values.Data));
        }
    }

    [Test]
    public void Build_BitPackerOutsideAnotherLayoutEndpoint_FailsRoleValidation()
    {
        var root = Group("Root", [Ref("bitpacker"), Ref("bytepacker")], Field("A", "sys.uint8"));

        Assert.That(() => Build(root), Throws.TypeOf<ExecutionPlanException>().With.Message.Contains("chained"));
    }

    private sealed class Values : IValueSource, IValueSink
    {
        public Dictionary<string, object?> Data { get; } = [];
        public List<string> Requests { get; } = [];
        public int Count { get; init; }

        public SourceResult GetField(FieldContext context)
        {
            var key = $"{context.Path}{context.Instance}";
            Requests.Add(key);
            if (Data.TryGetValue(key, out var value))
            {
                return SourceResult.Provided(new(context.Name, value?.GetType() ?? typeof(object), value));
            }
            return Count < 0 ? SourceResult.EndOfData() : SourceResult.NoValue();
        }

        public void SetField(FieldContext context, LogicalField value) => Data[$"{context.Path}{context.Instance}"] = value.Value;
        public IValueSource EnterGroup(GroupContext context) => this;
        IValueSink IValueSink.EnterGroup(GroupContext context) => this;
        public int GetCount(RepeatContext context) => Count;
        public IValueSource EnterItem(RepeatContext context, int index) => this;
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public int GetSelectedIndex(ChoiceContext context) => 0;
        public IValueSource EnterChoice(ChoiceContext context) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }
}
