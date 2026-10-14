using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class PackedGroupPlanTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void Build_PackedGroup_PreservesLogicalFieldsAndCarrierMetadata(bool root)
    {
        var packed = Packed(Ref("pack"), Ref("layout"));
        packed.ProcessorsList[0] = new SchemaProcessorRef
        {
            Processor = "test.pack", PublishNamespace = "flags",
            PropertyList = [new SchemaProperty { Name = "order", Value = "big" }]
        };
        packed.MemberList.Add(Field("Mode", Ref("value")));
        packed.MemberList.Add(Field("Channel"));
        SchemaGroup schema = root ? packed : new SchemaGroup { Name = "Root", MemberList = [packed] };
        var plan = Build(schema);
        var info = root ? (PackedGroupInfo)plan.Root : (PackedGroupInfo)plan.Root.Members.Single();

        Assert.That(info.Path.ToString(), Is.EqualTo(root ? "Flags" : "Root.Flags"));
        Assert.That(info.Group, Is.SameAs(packed));
        Assert.That(info.ByteLength.Value, Is.EqualTo(2));
        Assert.That(info.ByteSize.Value, Is.EqualTo(4));
        Assert.That(info.PackingProcessor.Processor, Is.TypeOf<Packer>());
        Assert.That(info.PackingProcessor.PublishNamespace, Is.EqualTo("flags"));
        Assert.That(info.PackingProcessor.Properties.Single().Value, Is.EqualTo("big"));
        Assert.That(info.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Mode", "Channel" }));
        Assert.That(info.Pipeline.LayoutProcessors.Single().Processor.Key.ToString(), Is.EqualTo("test.layout"));
        for (var i = 0; i < info.Fields.Count; i++)
        {
            var field = info.Fields[i];
            Assert.That(info.Members[i], Is.SameAs(field));
            Assert.That(field.Parent, Is.SameAs(info));
            Assert.That(field.Index, Is.EqualTo(i));
            Assert.That(field.Path.ToString(), Is.EqualTo(info.Path + "." + field.Name));
            Assert.That(field.Pipeline.PackingProcessors, Is.Empty);
            Assert.That(field.Pipeline.FieldProcessors, Is.Empty);
            Assert.That(field.Pipeline.LayoutProcessors, Is.SameAs(info.Pipeline.LayoutProcessors));
            Assert.That(field.Pipeline.OwnLayoutProcessors, Is.Empty);
        }
        Assert.That(info.Fields[0].Pipeline.ValueProcessors.Single().Processor.Key.ToString(), Is.EqualTo("test.value"));
        Assert.That(info.Fields[1].Pipeline.ValueProcessors, Is.Empty);
    }

    [Test]
    public void Build_PackerDoesNotLeakToChildrenOrSiblings()
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(Field("Mode"));
        var root = new SchemaGroup { Name = "Root", MemberList = [packed, Field("Next")] };
        var plan = Build(root);
        var info = (PackedGroupInfo)plan.Root.Members[0];
        Assert.That(info.Fields[0].Pipeline.PackingProcessors, Is.Empty);
        Assert.That(plan.Root.Pipeline.PackingProcessors, Is.Empty);
        Assert.That(plan.Root.Members[1].Pipeline.PackingProcessors, Is.Empty);
        Assert.That(info.Fields[0].Pipeline, Is.Not.SameAs(info.Pipeline));
    }

    [Test]
    public void Pipeline_PackingStageIsNeverInherited()
    {
        var parent = new ProcessorPipeline([new ProcessorBinding(new Packer("pack"), [])]);
        var child = new ProcessorPipeline(parent, []);
        Assert.That(parent.PackingProcessors, Has.Count.EqualTo(1));
        Assert.That(child.PackingProcessors, Is.Empty);
        Assert.That(child.ValueProcessors, Is.SameAs(parent.ValueProcessors));
        Assert.That(child.LayoutProcessors, Is.SameAs(parent.LayoutProcessors));
        Assert.That(() => DefaultProcessors.For(PipelineStage.Packing), Throws.InvalidOperationException.With.Message.Contains("no default packing processor"));
    }

    [TestCase(0)]
    [TestCase(2)]
    [TestCase(3)]
    public void Build_RequiresExactlyOnePacker(int count)
    {
        var processors = Enumerable.Range(0, count).Select(i => Ref(i == 1 ? "otherpack" : "pack")).ToArray();
        AssertInvalid(Packed(processors), "Flags", "exactly one packing processor");
    }

    [TestCase("field")]
    [TestCase("group")]
    [TestCase("repeat")]
    [TestCase("choice")]
    public void Build_PackerOnOtherNodeKinds_IsRejected(string kind)
    {
        var processor = Ref("pack");
        SchemaNode node = kind switch
        {
            "field" => Field("Wrong", processor),
            "repeat" => new SchemaRepeat { Name = "Wrong", Count = 1, ProcessorsList = [processor] },
            "choice" => new SchemaChoice { Name = "Wrong", SelectedIndex = 0, ProcessorsList = [processor], MemberList = [Field("A")] },
            _ => new SchemaGroup { Name = "Wrong", ProcessorsList = [processor] }
        };
        AssertInvalid(new SchemaGroup { Name = "Root", MemberList = [node] }, "Root.Wrong", "only be declared on a packedGroup");
    }

    [Test]
    public void Build_PackingStageWithoutInterface_IsRejected()
    {
        AssertInvalid(Packed(Ref("badpack")), "Flags", "does not implement its stage interface");
    }

    [Test]
    public void Build_PackerRequiredProperties_AreValidated()
    {
        AssertInvalid(Packed(Ref("requiredpack")), "Flags", "order");
    }

    [TestCase("field")]
    [TestCase("layout")]
    [TestCase("stream")]
    [TestCase("pack")]
    public void Build_ChildNonValueProcessor_IsRejected(string processor)
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(Field("Mode", Ref(processor)));
        AssertInvalid(packed, "Flags.Mode", processor == "pack" ? "only be declared on a packedGroup" : "only declare value processors");
    }

    [Test]
    public void Build_InheritedFieldRepresentation_IsRejected()
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(Field("Mode"));
        AssertInvalid(new SchemaGroup { Name = "Root", ProcessorsList = [Ref("field")], MemberList = [packed] },
            "Root.Flags", "cannot use field processors");
    }

    [Test]
    public void Build_InheritedValueAndLayoutStages_AreRetained()
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(Field("Mode"));
        var root = new SchemaGroup { Name = "Root", ProcessorsList = [Ref("value"), Ref("layout")], MemberList = [packed] };
        var plan = Build(root);
        var info = (PackedGroupInfo)plan.Root.Members[0];
        Assert.That(info.Pipeline.LayoutProcessors, Is.SameAs(plan.Root.Pipeline.LayoutProcessors));
        Assert.That(info.Pipeline.OwnLayoutProcessors, Is.Empty);
        Assert.That(info.Fields[0].Pipeline.ValueProcessors, Is.SameAs(plan.Root.Pipeline.ValueProcessors));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Build_DataTypeProcessors_AreValidatedAfterCompilation(bool invalid)
    {
        var document = new SchemaDocumentBuilder("Doc")
            .DataTypeDef("Scaled", "sys.uint8", processors: p => p.Add(invalid ? "test.field" : "test.value"))
            .NodeDef("FlagsDef", SchemaNodeKind.PackedGroup, g => g.Field("Mode", "Scaled"), processors: p => p.Add("test.pack"))
            .Group("Root", g => g.PackedGroup("Flags", 2, nodeDef: "FlagsDef"))
            .Build();
        var schemas = new SchemaSetBuilder();
        schemas.AddDocument(document);
        var set = schemas.Compile();
        if (invalid)
        {
            var exception = Assert.Throws<ExecutionPlanException>(() => ExecutionPlan.Create(set, "Doc.Root", Manager()));
            Assert.That(exception!.Message, Does.Contain("Root.Flags.Mode").And.Contain("only declare value processors"));
        }
        else
        {
            var info = (PackedGroupInfo)ExecutionPlan.Create(set, "Doc.Root", Manager()).Root.Members.Single();
            Assert.That(info.Fields.Single().DataType.Name.FullName, Is.EqualTo("Doc.Scaled"));
            Assert.That(info.Fields.Single().Pipeline.ValueProcessors.Single().Processor.Key.ToString(), Is.EqualTo("test.value"));
            Assert.That(info.PackingProcessor.Processor.Key.ToString(), Is.EqualTo("test.pack"));
        }
    }

    [Test]
    public void Build_DirectSchemaMissingCarrierLength_DefaultsToOneByte()
    {
        var packed = new SchemaPackedGroup { Name = "Flags", ProcessorsList = [Ref("pack")] };
        packed.MemberList.Add(new SchemaField { Name = "Mode", DataType = "sys.uint8" });
        var root = new SchemaGroup { Name = "Root" };
        root.MemberList.Add(packed);

        var info = (PackedGroupInfo)Build(root).Root.Members.Single();

        Assert.That(info.ByteLength is int length && length == 1, Is.True);
    }

    [TestCase("zero")]
    [TestCase("negative")]
    public void Build_DirectSchemaInvalidCarrierLength_IsRejected(string length)
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = length == "zero" ? 0 : -1,
            ProcessorsList = [Ref("pack")]
        };
        AssertInvalid(packed, "Flags", "must be positive");
    }

    [TestCase("byteLength")]
    [TestCase("byteOffset")]
    public void Build_DirectSchemaChildPhysicalAttributes_AreRejected(string attribute)
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(new SchemaField
        {
            Name = "Mode", DataType = "sys.uint8",
            ByteLength = attribute == "byteLength" ? 1 : default(SchemaValueOrRef<int>),
            ByteOffset = attribute == "byteOffset" ? 0 : null
        });
        AssertInvalid(packed, "Flags.Mode", "cannot declare byteLength or byteOffset");
    }

    [Test]
    public void Build_DirectSchemaStructuralChild_IsRejected()
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(new SchemaGroup { Name = "Nested" });
        AssertInvalid(packed, "Flags.Nested", "only contain fields");
    }

    [Test]
    public void Build_LogicalBytesFields_DoNotNeedPhysicalWindowsOrLastPosition()
    {
        var packed = new SchemaPackedGroup { Name = "Flags", ByteLength = 2, ProcessorsList = [Ref("pack")] };
        packed.MemberList.Add(new SchemaField { Name = "Bytes", DataType = "sys.bytes" });
        packed.MemberList.Add(Field("Mode"));
        var plan = Build(new SchemaGroup { Name = "Root", MemberList = [packed, Field("Next")] });
        Assert.That(((PackedGroupInfo)plan.Root.Members[0]).Fields.Select(f => f.Name), Is.EqualTo(new[] { "Bytes", "Mode" }));
        Assert.That(plan.Root.Members[1].Name, Is.EqualTo("Next"));
    }

    [Test]
    public void Build_CarrierLengthAndChildValueReferences_PublishLogicalFields()
    {
        var prefix = Field("Length");
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = new SchemaNodeRef { Path = "Root.Length" },
            ProcessorsList = [Ref("pack")], MemberList = [Field("Mode"), Field("Channel")]
        };
        var later = new SchemaField { Name = "Copy", DataType = "sys.uint8", Value = new SchemaNodeRef { Path = "Root.Flags.Mode" } };
        var plan = Build(new SchemaGroup { Name = "Root", MemberList = [prefix, packed, later] });
        var info = (PackedGroupInfo)plan.Root.Members[1];
        Assert.That(info.ByteLength.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Length")));
        Assert.That(((FieldInfo)plan.Root.Members[0]).PublishesValue, Is.True);
        Assert.That(info.Fields[0].PublishesValue, Is.True);
        Assert.That(info.Fields[1].PublishesValue, Is.False);
        Assert.That(((FieldInfo)plan.Root.Members[2]).ValueReference, Is.EqualTo(PublishedValueKey.ForPath("Root.Flags.Mode")));
    }

    [Test]
    public void Build_PublishedCarrierLength_IsBound()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = new SchemaPubRef { Namespace = "header", Name = "length" },
            ProcessorsList = [Ref("pack")]
        };
        Assert.That(((PackedGroupInfo)Build(packed).Root).ByteLength.Value, Is.EqualTo(new PublishedValueKey("header", "length")));
    }

    [Test]
    public void Build_CarrierLengthCannotDependOnChild()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = new SchemaNodeRef { Path = "Flags.Mode" },
            ProcessorsList = [Ref("pack")], MemberList = [Field("Mode")]
        };
        AssertInvalid(packed, "Flags", "cannot depend on one of its own child values");
    }

    [Test]
    public void Build_UnknownCarrierLengthReference_IsRejected()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = new SchemaNodeRef { Path = "Missing" }, ProcessorsList = [Ref("pack")]
        };
        AssertInvalid(packed, "Flags", "does not match any node");
    }

    [Test]
    public void Build_DuplicatePackerUses_AreRejectedBeforePipelineDeduplication()
    {
        AssertInvalid(Packed(Ref("pack"), Ref("pack")), "Flags", "exactly one packing processor");
    }

    [Test]
    public void Build_CarrierReferencesInsideRepeat_PreserveInstanceTemplates()
    {
        var packed = new SchemaPackedGroup
        {
            Name = "Flags", ByteLength = new SchemaNodeRef
            {
                Path = "Root.Items.Length",
                Indices = [new SchemaInstanceIndex("Root.Items", SchemaInstanceIndex.Current)]
            },
            ProcessorsList = [Ref("pack")], MemberList = [Field("Mode")]
        };
        var repeat = new SchemaRepeat { Name = "Items", Count = 2, MemberList = [Field("Length"), packed] };
        var plan = Build(new SchemaGroup { Name = "Root", MemberList = [repeat] });
        var item = (RepeatInfo)plan.Root.Members.Single();
        var info = (PackedGroupInfo)item.Members[1];
        Assert.That(info.ByteLength.Value, Is.EqualTo(PublishedValueKey.ForPath("Root.Items.Length", new InstancePath([InstancePath.Current]))));
        Assert.That(((FieldInfo)item.Members[0]).PublishesValue, Is.True);
        Assert.That(info.Fields.Single().Parent, Is.SameAs(info));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Sessions_PackedPlans_InvokeThePacker(bool root)
    {
        var packed = Packed(Ref("pack"));
        packed.MemberList.Add(Field("Mode"));
        var plan = Build(root ? packed : new SchemaGroup { Name = "Root", MemberList = [packed] });
        var model = new TrackingFields();
        var output = new ArrayBufferWriter<byte>();
        var writer = new WriterSession(plan, output);
        Assert.That(() => writer.Write(model), Throws.TypeOf<NotSupportedException>().With.Message.Contains("Plan tests do not execute packing"));
        Assert.That(output.WrittenCount, Is.Zero);
        Assert.That(model.Reads, Is.EqualTo(1));
        var reader = new ReaderSession(plan);
        Assert.That(() => reader.Read(new ReadOnlySequence<byte>(new byte[4]), model),
            Throws.TypeOf<NotSupportedException>().With.Message.Contains("Plan tests do not execute packing"));
        Assert.That(model.Writes, Is.Zero);
        Assert.That(reader.BytesRead, Is.Zero);
    }

    private sealed class TrackingFields : IFieldSource, IFieldSink
    {
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public SourceResult GetField(FieldContext context)
        {
            Reads++;
            return SourceResult.Provided(new LogicalField(context.Node.Name, typeof(byte), (byte)1));
        }
        public void SetField(FieldContext context, LogicalField value) => Writes++;
    }

    private static SchemaPackedGroup Packed(params SchemaProcessorRef[] processors)
        => new() { Name = "Flags", ByteLength = 2, ByteSize = 4, ProcessorsList = [.. processors] };
    private static SchemaField Field(string name, params SchemaProcessorRef[] processors)
        => new() { Name = name, DataType = "sys.uint8", ProcessorsList = [.. processors] };
    private static SchemaProcessorRef Ref(string id) => new() { Processor = new SchemaName("test." + id) };
    private static ProcessorManager Manager()
    {
        var manager = new ProcessorManager();
        manager.Register(new Factory());
        return manager;
    }
    private static ExecutionPlan Build(SchemaGroup root) => ExecutionPlan.Create(root, Manager());
    private static void AssertInvalid(SchemaGroup root, string path, string message)
    {
        var exception = Assert.Throws<ExecutionPlanException>(() => Build(root));
        Assert.That(exception!.Message, Does.Contain(path).And.Contain(message));
    }

    private sealed class Factory : IProcessorFactory
    {
        public string Namespace => "test";
        public IProcessor? CreateProcessor(string id) => id switch
        {
            "pack" or "otherpack" or "requiredpack" => new Packer(id),
            "value" => new ValueProcessor(),
            "field" => DefaultProcessors.FieldProcessor,
            "layout" => new LayoutProcessor(),
            "stream" => DefaultProcessors.StreamProcessor,
            "badpack" => new BadPacker(),
            _ => null
        };
    }

    private sealed class Packer(string id) : IPackingProcessor
    {
        public ProcessorKey Key => new("test", id);
        public string Name => "Test packing";
        public PipelineStage Stage => PipelineStage.Packing;
        public IReadOnlyList<PropertyDescriptor> Properties => [new("order", "sys.string", id == "requiredpack")];
        public FieldWriteResult<EncodedField> Write(IReadOnlyList<LogicalField> fields, PackingProcessorContext context)
            => throw new NotSupportedException("Plan tests do not execute packing.");
        public FieldReadResult<IReadOnlyList<LogicalField>> Read(EncodedField carrier, PackingProcessorContext context)
            => throw new NotSupportedException("Plan tests do not execute packing.");
    }

    private sealed class BadPacker : IProcessor
    {
        public ProcessorKey Key => new("test.badpack");
        public string Name => "Bad packer";
        public PipelineStage Stage => PipelineStage.Packing;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
    }

    private sealed class ValueProcessor : IValueProcessor
    {
        public ProcessorKey Key => new("test.value");
        public string Name => "Test value";
        public PipelineStage Stage => PipelineStage.Semantic;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public LogicalField Write(LogicalField value, ValueProcessorContext context) => value;
        public LogicalField Read(LogicalField value, ValueProcessorContext context) => value;
    }

    private sealed class LayoutProcessor : ILayoutProcessor
    {
        public ProcessorKey Key => new("test.layout");
        public string Name => "Test layout";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
        public WriteResult Write(IBufferWriter<byte> writer, EncodedField field, LayoutProcessorContext context)
            => throw new NotSupportedException("Plan tests do not execute layout.");
        public LayoutReadResult<EncodedField> Read(ref SequenceReader<byte> reader, LayoutProcessorContext context)
            => throw new NotSupportedException("Plan tests do not execute layout.");
    }
}
