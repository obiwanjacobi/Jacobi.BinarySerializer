using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class PackedGroupSessionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_LogicalChildren_UseOneCarrier(bool nested)
    {
        var packed = Packed(2);
        var plan = BuildPlan(nested ? Group("Root", [], packed, Field("After", "sys.uint8")) : packed);
        var prefix = nested ? "Root.Flags" : "Flags";
        var source = new FlatValues(new() { [prefix + ".A"] = (byte)3, [prefix + ".B"] = (byte)12, ["Root.After"] = (byte)9 });
        var (bytes, sink) = RoundTrip(plan, source);
        Assert.That(bytes, Is.EqualTo(nested ? new byte[] { 0xC3, 0, 9 } : new byte[] { 0xC3, 0 }));
        Assert.That(sink.Values[prefix + ".A"], Is.EqualTo((byte)3));
        Assert.That(sink.Values[prefix + ".B"], Is.EqualTo((byte)12));
        Assert.That(source.Requests.Count(p => p == prefix + ".A"), Is.EqualTo(1));
        Assert.That(source.Requests.Count(p => p == prefix + ".B"), Is.EqualTo(1));
        Assert.That(sink.Values.Count, Is.EqualTo(nested ? 3 : 2));
    }

    [Test]
    public void RoundTrip_StructuredScopes_AreEnteredAndCompleted()
    {
        var plan = BuildPlan(Group("Root", [], Packed(1)));
        var events = new List<string>();
        var output = new ArrayBufferWriter<byte>();
        var source = new DictSource(new() { ["Root.Flags.A"] = (byte)3, ["Root.Flags.B"] = (byte)12 }, events);
        Assert.That(new WriterSession(plan, output).Write(source), Is.EqualTo(WriteResult.Success));
        Assert.That(events, Is.EqualTo(new[] { "Enter:Root.Flags" }));
        var sink = new DictSink();
        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Events, Is.EqualTo(new[] { "Enter:Root.Flags", "Complete:Root.Flags", "Complete:Root" }));
        Assert.That(sink.Values["Root.Flags.A"], Is.EqualTo((byte)3));
        Assert.That(sink.Values["Root.Flags.B"], Is.EqualTo((byte)12));
    }

    [Test]
    public void RoundTrip_ValueProcessors_RunOutsidePacking()
    {
        var packed = Packed(1);
        packed.MemberList[0] = Field("A", "sys.decimal", [Ref("scale", ("scale", "10"))]);
        var (bytes, sink) = RoundTrip(BuildPlan(packed), new FlatValues(new() { ["Flags.A"] = 0.3m, ["Flags.B"] = (byte)12 }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC3 }));
        Assert.That(sink.Values["Flags.A"], Is.EqualTo(0.3m));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_ExpectedValues_RespectModelOption(bool useModel)
    {
        var packed = Packed(1);
        packed.MemberList[0] = new SchemaField { Name = "A", DataType = "sys.uint8", Value = "3" };
        var plan = BuildPlan(packed);
        var source = new FlatValues(new() { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 });
        var (bytes, sink) = RoundTrip(plan, source, useModel);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC3 }));
        Assert.That(source.Requests.Contains("Flags.A"), Is.EqualTo(useModel));
        Assert.That(sink.Values.ContainsKey("Flags.A"), Is.EqualTo(useModel));
        Assert.That(sink.Values["Flags.B"], Is.EqualTo((byte)12));
        var wrong = new FlatValues();
        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xC4 }), wrong),
            Throws.InvalidOperationException.With.Message.Contains("schema requires"));
    }

    [Test]
    public void RoundTrip_ChildReferences_ArePublishedInOrder()
    {
        var packed = Packed(1);
        packed.MemberList[1] = new SchemaField { Name = "B", DataType = "sys.uint8", Value = new SchemaNodeRef { Path = "Root.Flags.A" } };
        var copy = new SchemaField { Name = "Copy", DataType = "sys.uint8", Value = new SchemaNodeRef { Path = "Root.Flags.A" } };
        var (bytes, sink) = RoundTrip(BuildPlan(Group("Root", [], packed, copy)),
            new FlatValues(new() { ["Root.Flags.A"] = (byte)3 }), true);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x33, 3 }));
        Assert.That(sink.Values["Root.Flags.A"], Is.EqualTo((byte)3));
        Assert.That(sink.Values["Root.Flags.B"], Is.EqualTo((byte)3));
        Assert.That(sink.Values["Root.Copy"], Is.EqualTo((byte)3));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_ByteOrder_AppliesToWholeCarrier(bool follower)
    {
        var packed = Packed(2, Ref("bytepacker", ("byteorder", "big")));
        var root = follower ? Group("Root", [Ref("align", ("bytes", "1"))], packed) : packed;
        var prefix = follower ? "Root.Flags" : "Flags";
        var (bytes, sink) = RoundTrip(BuildPlan(root), new FlatValues(new() { [prefix + ".A"] = (byte)3, [prefix + ".B"] = (byte)12 }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 0, 0xC3 }));
        Assert.That(sink.Values[prefix + ".A"], Is.EqualTo((byte)3));
        Assert.That(sink.Values[prefix + ".B"], Is.EqualTo((byte)12));
    }

    [Test]
    public void RoundTrip_AlignmentAndCrc_CountGroupBytesSeparately()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = 1, ByteSize = 6,
            ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }, Ref("bytepacker"), Ref("align", ("bytes", "2")), Ref("crc")],
            MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
        };
        var plan = BuildPlan(packed);
        var (bytes, sink) = RoundTrip(plan, new FlatValues(new() { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 }));
        Assert.That(bytes.Length, Is.EqualTo(6));
        Assert.That(bytes[0], Is.EqualTo(0xC3));
        Assert.That(bytes[1], Is.Zero);
        Assert.That(sink.Values["Flags.B"], Is.EqualTo((byte)12));
        bytes[0] ^= 1;
        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), new FlatValues()), Throws.InvalidOperationException);
    }

    [Test]
    public void RoundTrip_DeferredGroupSize_IsDerivedFromCarrierAndCrc()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = 1, ByteSize = new SchemaNodeRef { Path = "Root.Length" },
            ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }, Ref("crc")],
            MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
        };
        var root = Group("Root", [], Field("Length", "sys.uint8"), packed);
        var (bytes, sink) = RoundTrip(BuildPlan(root), new FlatValues(new() { ["Root.Flags.A"] = (byte)3, ["Root.Flags.B"] = (byte)12 }));
        Assert.That(bytes.Length, Is.EqualTo(6));
        Assert.That(bytes[0], Is.EqualTo(5));
        Assert.That(bytes[1], Is.EqualTo(0xC3));
        Assert.That(sink.Values["Root.Length"], Is.EqualTo((byte)5));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_CarrierLengthReference_IsResolved(bool published)
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = published ? new SchemaPubRef { Namespace = "test.pack", Name = "length" } : new SchemaNodeRef { Path = "Root.Length" },
            ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }],
            MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
        };
        var root = published ? Group("Root", [], Packed(1), packed) : Group("Root", [], Field("Length", "sys.uint8"), packed);
        if (published)
        {
            root.MemberList[0] = new SchemaPackedGroup
            {
                Name = "Header", ByteLength = 1, ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }],
                MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
            };
        }
        var (bytes, sink) = RoundTrip(BuildPlan(root), new FlatValues(new()
        {
            ["Root.Length"] = (byte)1, ["Root.Flags.A"] = (byte)3, ["Root.Flags.B"] = (byte)12,
            ["Root.Header.A"] = (byte)1, ["Root.Header.B"] = (byte)0
        }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0xC3 }));
        Assert.That(sink.Values["Root.Flags.B"], Is.EqualTo((byte)12));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_Repeats_UseIndependentCarrierInstances(bool open)
    {
        var repeat = new SchemaRepeat
        {
            Name = "Items", Count = open ? default(SchemaValueOrRef<int>) : 2, MemberList = [Packed(1)]
        };
        var values = new FlatValues(new()
        {
            ["Root.Items.Flags.A[0]"] = (byte)3, ["Root.Items.Flags.B[0]"] = (byte)12,
            ["Root.Items.Flags.A[1]"] = (byte)5, ["Root.Items.Flags.B[1]"] = (byte)10
        }, endWhenMissing: open);
        var (bytes, sink) = RoundTrip(BuildPlan(Group("Root", [], repeat)), values);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC3, 0xA5 }));
        Assert.That(sink.Values["Root.Items.Flags.A[0]"], Is.EqualTo((byte)3));
        Assert.That(sink.Values["Root.Items.Flags.B[1]"], Is.EqualTo((byte)10));
        Assert.That(sink.Values.Count, Is.EqualTo(4));
    }

    [Test]
    public void Read_OpenRepeat_PartialCarrierIsNotCleanEnd()
    {
        var repeat = new SchemaRepeat { Name = "Items", MemberList = [Group("Body", [], Packed(2))] };
        var plan = BuildPlan(Group("Root", [], repeat));
        var sink = new FlatValues();
        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3 }), sink), Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(sink.Values, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundTrip_OpenRepeat_LeavesReservedCrcTail(bool sized)
    {
        var repeat = new SchemaRepeat { Name = "Items", MemberList = [Packed(2)] };
        var root = new SchemaGroup
        {
            Name = "Root", ByteSize = sized ? 8 : default(SchemaValueOrRef<int>),
            ProcessorsList = [Ref("crc")], MemberList = [repeat]
        };
        var (bytes, sink) = RoundTrip(BuildPlan(root), new FlatValues(new()
        {
            ["Root.Items.Flags.A[0]"] = (byte)3, ["Root.Items.Flags.B[0]"] = (byte)12,
            ["Root.Items.Flags.A[1]"] = (byte)5, ["Root.Items.Flags.B[1]"] = (byte)10
        }, endWhenMissing: true));
        Assert.That(bytes.Length, Is.EqualTo(8));
        Assert.That(bytes.Take(4), Is.EqualTo(new byte[] { 0xC3, 0, 0xA5, 0 }));
        Assert.That(sink.Values.Count, Is.EqualTo(4));
        Assert.That(sink.Values["Root.Items.Flags.B[1]"], Is.EqualTo((byte)10));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Read_PartialCarrier_CannotConsumeReservedTail(bool sized)
    {
        var repeat = new SchemaRepeat { Name = "Items", MemberList = [Packed(2)] };
        var root = new SchemaGroup
        {
            Name = "Root", ByteSize = sized ? 7 : default(SchemaValueOrRef<int>),
            ProcessorsList = [Ref("crc")], MemberList = [repeat]
        };
        var sink = new FlatValues();
        Assert.That(new ReaderSession(BuildPlan(root)).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3, 0, 0xA5, 1, 2, 3, 4 }), sink),
            Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(sink.Values.Keys, Is.EquivalentTo(new[] { "Root.Items.Flags.A[0]", "Root.Items.Flags.B[0]" }));
        Assert.That(sink.Values["Root.Items.Flags.B[0]"], Is.EqualTo((byte)12));
    }

    [Test]
    public void RoundTrip_Choice_SelectsPackedAlternative()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = 0, MemberList = [Packed(1), Field("Other", "sys.uint8")] };
        var (bytes, sink) = RoundTrip(BuildPlan(Group("Root", [], choice)),
            new FlatValues(new() { ["Root.Pick.Flags.A"] = (byte)3, ["Root.Pick.Flags.B"] = (byte)12 }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC3 }));
        Assert.That(sink.Values.Keys, Is.EquivalentTo(new[] { "Root.Pick.Flags.A", "Root.Pick.Flags.B" }));
    }

    [Test]
    public void Range_WholeCarrierOrExcludedGroup_IsSupported()
    {
        var plan = BuildPlan(Group("Root", [], Field("Before", "sys.uint8"), Packed(1), Field("After", "sys.uint8")));
        var range = plan.CreateRange("Root.Flags", "Root.Flags");
        var (bytes, sink) = RoundTrip(plan, new FlatValues(new() { ["Root.Flags.A"] = (byte)3, ["Root.Flags.B"] = (byte)12 }), range: range);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xC3 }));
        Assert.That(sink.Values.Count, Is.EqualTo(2));
        var after = plan.CreateRange("Root.After", "Root.After");
        var (afterBytes, afterSink) = RoundTrip(plan, new FlatValues(new() { ["Root.After"] = (byte)9 }), range: after);
        Assert.That(afterBytes, Is.EqualTo(new byte[] { 9 }));
        Assert.That(afterSink.Values.Keys, Is.EqualTo(new[] { "Root.After" }));
        Assert.That(() => plan.CreateRange("Root.Flags.A", "Root.Flags.A"), Throws.ArgumentException.With.Message.Contains("all fields"));
        Assert.That(() => plan.CreateRange("Root.Flags.B", "Root.After"), Throws.ArgumentException.With.Message.Contains("all fields"));
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Read_TruncatedCarrier_DoesNotScatter(int length)
    {
        var sink = new FlatValues();
        Assert.That(new ReaderSession(BuildPlan(Packed(2))).Read(new ReadOnlySequence<byte>(new byte[length]), sink), Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(sink.Values, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Read_CarrierCannotCrossEnclosingSizeWindow(bool ownWindow)
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = 2, ByteSize = ownWindow ? 2 : default(SchemaValueOrRef<int>),
            ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }],
            MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
        };
        var root = new SchemaGroup { Name = "Root", ByteSize = 1, MemberList = [packed] };
        var sink = new FlatValues();
        Assert.That(new ReaderSession(BuildPlan(root)).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3, 0 }), sink), Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(sink.Values, Is.Empty);
    }

    [TestCase("memory", false)]
    [TestCase("memory", true)]
    [TestCase("readOnlyMemory", false)]
    [TestCase("readOnlyMemory", true)]
    public void RoundTrip_MemoryCarrier_UsesExistingLayoutHeads(string mode, bool crcHead)
    {
        var layouts = crcHead ? new[] { Ref("crc") } : new[] { Ref("bytepacker", ("byteorder", "big")), Ref("crc") };
        var (bytes, sink) = RoundTrip(BuildPlan(Packed(2, layouts), mode),
            new FlatValues(new() { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 }));
        Assert.That(bytes.Length, Is.EqualTo(6));
        Assert.That(bytes.Take(2), Is.EqualTo(crcHead ? new byte[] { 0xC3, 0 } : new byte[] { 0, 0xC3 }));
        Assert.That(sink.Values["Flags.B"], Is.EqualTo((byte)12));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(268435456)]
    public void ReferencedCarrierLength_InvalidAtRuntime_FailsBeforePacking(int length)
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = new SchemaNodeRef { Path = "Root.Length" },
            ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }],
            MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
        };
        var plan = BuildPlan(Group("Root", [], Field("Length", "sys.int32"), packed));
        var source = new FlatValues(new() { ["Root.Length"] = length });
        Assert.That(() => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(source),
            Throws.InvalidOperationException.With.Message.Contains("packed byteLength"));
        Assert.That(source.Requests, Is.EqualTo(new[] { "Root.Length" }));
        var sink = new FlatValues();
        Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(BitConverter.GetBytes(length)), sink),
            Throws.InvalidOperationException.With.Message.Contains("packed byteLength"));
        Assert.That(sink.Values.Keys, Is.EqualTo(new[] { "Root.Length" }));
    }

    [Test]
    public void RoundTrip_NestedSizeWindow_DoesNotSubtractAncestorTrailer()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = 2, ByteSize = 2,
            ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }],
            MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
        };
        var (bytes, sink) = RoundTrip(BuildPlan(Group("Root", [Ref("crc")], packed)),
            new FlatValues(new() { ["Root.Flags.A"] = (byte)3, ["Root.Flags.B"] = (byte)12 }));
        Assert.That(bytes.Length, Is.EqualTo(6));
        Assert.That(sink.Values["Root.Flags.B"], Is.EqualTo((byte)12));
    }

    [Test]
    public void Range_RepeatInstance_EncodesOnlySelectedCarrier()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = 3, MemberList = [Packed(1)] };
        var plan = BuildPlan(Group("Root", [], repeat));
        var range = plan.CreateRange("Root.Items.Flags", new InstancePath([1]), "Root.Items.Flags", new InstancePath([1]));
        var (bytes, sink) = RoundTrip(plan,
            new FlatValues(new() { ["Root.Items.Flags.A[1]"] = (byte)5, ["Root.Items.Flags.B[1]"] = (byte)10 }), range: range);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xA5 }));
        Assert.That(sink.Values.Keys, Is.EquivalentTo(new[] { "Root.Items.Flags.A[1]", "Root.Items.Flags.B[1]" }));
    }

    [Test]
    public void RoundTrip_StreamStage_FramesThePackedPayload()
    {
        var (bytes, sink) = RoundTrip(BuildPlan(Packed(2, new SchemaProcessorRef { Processor = "test.frame" })),
            new FlatValues(new() { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xEE, 0xC3, 0 }));
        Assert.That(sink.Values["Flags.B"], Is.EqualTo((byte)12));
    }

    [Test]
    public void Sessions_ReusedWithSharedPlan_DoNotRetainCarrierData()
    {
        var plan = BuildPlan(Packed(2, Ref("crc")));
        var output = new ArrayBufferWriter<byte>();
        var writer = new WriterSession(plan, output);
        var reader = new ReaderSession(plan);
        foreach (var value in new byte[] { 3, 5 })
        {
            var start = output.WrittenCount;
            Assert.That(writer.Write(new FlatValues(new() { ["Flags.A"] = value, ["Flags.B"] = (byte)12 })), Is.EqualTo(WriteResult.Success));
            Assert.That(output.WrittenCount - start, Is.EqualTo(6));
            Assert.That(writer.BytesWritten, Is.EqualTo(output.WrittenCount));
            var sink = new FlatValues();
            Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory.Slice(start)), sink), Is.EqualTo(ReadResult.Success));
            Assert.That(reader.BytesRead, Is.EqualTo(6));
            Assert.That(sink.Values["Flags.A"], Is.EqualTo(value));
            Assert.That(sink.Values["Flags.B"], Is.EqualTo((byte)12));
        }
    }

    [TestCase("writeWidth")]
    [TestCase("writeBytes")]
    [TestCase("readWidth")]
    [TestCase("readCount")]
    [TestCase("readOrder")]
    public void InvalidPackerResults_FailExplicitly(string mode)
    {
        var plan = BuildPlan(Packed(1), mode);
        if (mode.StartsWith("write", StringComparison.Ordinal))
        {
            var output = new ArrayBufferWriter<byte>();
            Assert.That(() => new WriterSession(plan, output).Write(new FlatValues(new() { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 })),
                Throws.InvalidOperationException.With.Message.Contains("Flags"));
            Assert.That(output.WrittenCount, Is.Zero);
        }
        else
        {
            var sink = new FlatValues();
            Assert.That(() => new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3 }), sink),
                Throws.InvalidOperationException.With.Message.Contains("Flags"));
            Assert.That(sink.Values, Is.Empty);
        }
    }

    [Test]
    public void PackerFailure_PropagatesWithoutOutputOrScatter()
    {
        var plan = BuildPlan(Packed(1), "failure");
        var output = new ArrayBufferWriter<byte>();
        Assert.That(new WriterSession(plan, output).Write(new FlatValues(new() { ["Flags.A"] = (byte)3, ["Flags.B"] = (byte)12 })), Is.EqualTo(WriteResult.Failure));
        Assert.That(output.WrittenCount, Is.Zero);
        var sink = new FlatValues();
        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xC3 }), sink), Is.EqualTo(ReadResult.Failure));
        Assert.That(sink.Values, Is.Empty);
    }

    private static SchemaPackedGroup Packed(int byteLength, params SchemaProcessorRef[] layouts) => new()
    {
        Name = "Flags", ByteLength = byteLength,
        ProcessorsList = [new SchemaProcessorRef { Processor = "test.pack" }, .. layouts],
        MemberList = [Field("A", "sys.uint8"), Field("B", "sys.uint8")]
    };

    private static ExecutionPlan BuildPlan(SchemaGroup root, string mode = "valid")
    {
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        manager.Register(new Factory(mode));
        var dataTypes = Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        return ExecutionPlan.Create(root, manager, dataTypes);
    }

    private static (byte[] Bytes, FlatValues Sink) RoundTrip(ExecutionPlan plan, FlatValues source, bool useModel = false, PlanRange? range = null)
    {
        var output = new ArrayBufferWriter<byte>();
        var writer = new WriterSession(plan, output, valueFieldsUseModel: useModel);
        Assert.That(writer.Write(source, range), Is.EqualTo(WriteResult.Success));
        Assert.That(writer.BytesWritten, Is.EqualTo(output.WrittenCount));
        var sink = new FlatValues();
        var reader = new ReaderSession(plan, valueFieldsUseModel: useModel);
        Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink, range), Is.EqualTo(ReadResult.Success));
        Assert.That(reader.BytesRead, Is.EqualTo(output.WrittenCount));
        return (output.WrittenSpan.ToArray(), sink);
    }

    private sealed class FlatValues(Dictionary<string, object?>? values = null, bool endWhenMissing = false) : IFieldSource, IFieldSink
    {
        public Dictionary<string, object?> Values { get; } = values ?? [];
        public List<string> Requests { get; } = [];
        public SourceResult GetField(FieldContext context)
        {
            var key = context.Path + context.Instance.ToString();
            Requests.Add(key);
            return Values.TryGetValue(key, out var value)
                ? SourceResult.Provided(new LogicalField(context.Name, value?.GetType() ?? typeof(object), value))
                : endWhenMissing ? SourceResult.EndOfData() : SourceResult.NoValue();
        }
        public void SetField(FieldContext context, LogicalField value) => Values[context.Path + context.Instance.ToString()] = value.Value;
    }

    private sealed class Factory(string mode) : IProcessorFactory
    {
        public string Namespace => "test";
        public IProcessor? CreateProcessor(string id) => id switch
        {
            "pack" => new NibblePacker(mode),
            "frame" => new PrefixStream(),
            _ => null
        };
    }

    private sealed class PrefixStream : IStreamProcessor
    {
        public ProcessorKey Key => new("test.frame");
        public string Name => "Test prefix framing";
        public PipelineStage Stage => PipelineStage.Stream;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public WriteResult Write(ref SequenceReader<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
        {
            output.Write(new byte[] { 0xEE });
            output.Write(input.UnreadSequence.ToArray());
            input.Advance(input.Remaining);
            return WriteResult.Success;
        }
        public ReadResult Read(ref SequenceReader<byte> input, IBufferWriter<byte> output, StreamProcessorContext context)
        {
            if (!input.TryRead(out var prefix) || prefix != 0xEE) return ReadResult.Failure;
            output.Write(input.UnreadSequence.ToArray());
            input.Advance(input.Remaining);
            return ReadResult.Success;
        }
    }

    private sealed class NibblePacker(string mode) : IPackingProcessor
    {
        public ProcessorKey Key => new("test.pack");
        public string Name => "Test nibble packing";
        public PipelineStage Stage => PipelineStage.Packing;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public FieldWriteResult<EncodedField> Write(IReadOnlyList<LogicalField> fields, PackingProcessorContext context)
        {
            if (mode == "failure") return FieldWriteResult<EncodedField>.Failure();
            Assert.That(fields.Select(f => f.Name), Is.EqualTo(context.Fields.Select(f => f.Name)));
            var bytes = new byte[context.ByteLength];
            bytes[0] = (byte)(Convert.ToByte(fields[0].Value) | Convert.ToByte(fields[1].Value) << 4);
            context.Publish("length", context.ByteLength);
            var bits = mode == "writeWidth" ? context.ByteLength * 8 - 1 : context.ByteLength * 8;
            if (mode == "writeBytes") bytes = [];
            object value = mode switch
            {
                "memory" => bytes.AsMemory(),
                "readOnlyMemory" => new ReadOnlyMemory<byte>(bytes),
                _ => bytes
            };
            return FieldWriteResult<EncodedField>.Written(new(context.Group.Name, value.GetType(), value, bits), bits);
        }
        public FieldReadResult<IReadOnlyList<LogicalField>> Read(EncodedField carrier, PackingProcessorContext context)
        {
            if (mode == "failure") return FieldReadResult<IReadOnlyList<LogicalField>>.Failure();
            var bytes = (byte[])carrier.Value!;
            Assert.That(bytes.Length, Is.EqualTo(context.ByteLength));
            context.Publish("length", context.ByteLength);
            IReadOnlyList<LogicalField> fields = [
                new(context.Fields[0].Name, typeof(byte), (byte)(bytes[0] & 15)),
                new(context.Fields[1].Name, typeof(byte), (byte)(bytes[0] >> 4))
            ];
            if (mode == "readCount") fields = [];
            if (mode == "readOrder") fields = fields.Reverse().ToArray();
            return FieldReadResult<IReadOnlyList<LogicalField>>.Consumed(fields, mode == "readWidth" ? 0 : context.ByteLength * 8);
        }
    }
}
