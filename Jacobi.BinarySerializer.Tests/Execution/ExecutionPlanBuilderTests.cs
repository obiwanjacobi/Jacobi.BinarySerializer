using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Execution;

public class ExecutionPlanBuilderTests
{
    private const string Ns = "test";

    [Test]
    public void Build_NullRoot_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.Build((SchemaGroup)null!));
    }

    [Test]
    public void Build_GroupWithFields_CreatesTreeWithPathsParentAndIndex()
    {
        var root = CreateGroup("Root");
        var inner = CreateGroup("Inner");
        root.ChildList.Add(CreateField("A"));
        root.ChildList.Add(inner);
        inner.ChildList.Add(CreateField("B"));

        var plan = CreateBuilder().Build(root);

        Assert.That(plan.Root.Path, Is.EqualTo("Root"));
        Assert.That(plan.Root.Parent, Is.Null);
        Assert.That(plan.Root.Children, Has.Count.EqualTo(2));

        var a = (FieldInfo)plan.Root.Children[0];
        var innerInfo = (GroupInfo)plan.Root.Children[1];
        var b = (FieldInfo)innerInfo.Children[0];

        Assert.That(a.Path, Is.EqualTo("Root.A"));
        Assert.That(a.Index, Is.EqualTo(0));
        Assert.That(a.Parent, Is.SameAs(plan.Root));
        Assert.That(innerInfo.Index, Is.EqualTo(1));
        Assert.That(b.Path, Is.EqualTo("Root.Inner.B"));
        Assert.That(b.Parent, Is.SameAs(innerInfo));
    }

    [Test]
    public void Build_NodeWithoutProcessors_SharesParentPipeline()
    {
        var root = CreateGroup("Root", Ref("layout"));
        root.ChildList.Add(CreateField("A"));

        var plan = CreateBuilder().Build(root);

        Assert.That(plan.Root.Children[0].Pipeline, Is.SameAs(plan.Root.Pipeline));
    }

    [Test]
    public void Build_RootWithoutProcessors_HasEmptyStages()
    {
        var plan = CreateBuilder().Build(CreateGroup("Root"));

        var pipeline = plan.Root.Pipeline;
        Assert.That(pipeline.ValueProcessors, Is.Empty);
        Assert.That(pipeline.FieldProcessors, Is.Empty);
        Assert.That(pipeline.LayoutProcessors, Is.Empty);
        Assert.That(pipeline.StreamProcessors, Is.Empty);
    }

    [Test]
    public void Build_FieldProcessor_SortedIntoStageAndInheritsOtherStages()
    {
        var root = CreateGroup("Root", Ref("layout"));
        root.ChildList.Add(CreateField("A", Ref("value")));

        var plan = CreateBuilder().Build(root);

        var field = plan.Root.Children[0];
        Assert.That(field.Pipeline, Is.Not.SameAs(plan.Root.Pipeline));
        Assert.That(field.Pipeline.ValueProcessors, Has.Count.EqualTo(1));
        Assert.That(field.Pipeline.ValueProcessors[0].Processor.Key, Is.EqualTo(new ProcessorKey(Ns, "value")));
        Assert.That(field.Pipeline.LayoutProcessors, Is.SameAs(plan.Root.Pipeline.LayoutProcessors));
    }

    [Test]
    public void Build_DuplicateProcessorInSameStage_IsIgnored()
    {
        var root = CreateGroup("Root", Ref("layout"), Ref("layout"));

        var plan = CreateBuilder().Build(root);

        Assert.That(plan.Root.Pipeline.LayoutProcessors, Has.Count.EqualTo(1));
    }

    [Test]
    public void Build_ProcessorProperties_ArePassedToBinding()
    {
        var processorRef = Ref("layout");
        processorRef.PropertyList.Add(new SchemaProperty { Name = "endian", Value = "big" });
        var root = CreateGroup("Root", processorRef);

        var plan = CreateBuilder().Build(root);

        var binding = plan.Root.Pipeline.LayoutProcessors.Single();
        Assert.That(binding.Properties.Single().Name, Is.EqualTo("endian"));
        Assert.That(binding.Properties.Single().Value, Is.EqualTo("big"));
    }

    [Test]
    public void Build_Repeat_WithConstantCount_CreatesRepeatInfo()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = 3 };
        repeat.ChildList.Add(CreateField("A"));
        var root = CreateGroup("Root");
        root.ChildList.Add(repeat);

        var plan = CreateBuilder().Build(root);

        var info = (RepeatInfo)plan.Root.Children[0];
        Assert.That(info.Count.Value, Is.EqualTo(3));
        Assert.That(info.Children, Has.Count.EqualTo(1));
    }

    [Test]
    public void Build_Repeat_WithValueRefCount_BindsPublishedKey()
    {
        var repeat = new SchemaRepeat { Name = "Items", Count = new SchemaValueRef { Reference = "hdr/count" } };
        var root = CreateGroup("Root");
        root.ChildList.Add(repeat);

        var plan = CreateBuilder().Build(root);

        var info = (RepeatInfo)plan.Root.Children[0];
        Assert.That(info.Count.Value, Is.EqualTo(new PublishedValueKey("hdr", "count")));
    }

    [Test]
    public void Build_Choice_IndexInRange_CreatesChoiceInfo()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = 1 };
        choice.ChildList.Add(CreateField("A"));
        choice.ChildList.Add(CreateField("B"));
        var root = CreateGroup("Root");
        root.ChildList.Add(choice);

        var plan = CreateBuilder().Build(root);

        var info = (ChoiceInfo)plan.Root.Children[0];
        Assert.That(info.SelectedIndex.Value, Is.EqualTo(1));
    }

    [TestCase(-1)]
    [TestCase(2)]
    public void Build_Choice_IndexOutOfRange_Throws(int index)
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = index };
        choice.ChildList.Add(CreateField("A"));
        choice.ChildList.Add(CreateField("B"));
        var root = CreateGroup("Root");
        root.ChildList.Add(choice);

        var ex = Assert.Throws<ExecutionPlanException>(() => CreateBuilder().Build(root));

        Assert.That(ex!.Errors, Has.Count.EqualTo(1));
        Assert.That(ex.Errors[0], Does.Contain("Root.Pick").And.Contain("out of range"));
    }

    [Test]
    public void Build_ProcessorStageMismatch_ThrowsWithPath()
    {
        var root = CreateGroup("Root");
        root.ChildList.Add(CreateField("A", Ref("bad")));

        var ex = Assert.Throws<ExecutionPlanException>(() => CreateBuilder().Build(root));

        Assert.That(ex!.Errors, Has.Count.EqualTo(1));
        Assert.That(ex.Errors[0], Does.Contain("Root.A").And.Contain("does not implement"));
    }

    [Test]
    public void Build_MultipleErrors_AreAllReported()
    {
        var root = CreateGroup("Root");
        root.ChildList.Add(CreateField("A", Ref("bad")));
        root.ChildList.Add(CreateField("B", Ref("bad")));

        var ex = Assert.Throws<ExecutionPlanException>(() => CreateBuilder().Build(root));

        Assert.That(ex!.Errors, Has.Count.EqualTo(2));
    }

    [Test]
    public void Build_FromSchemaSet_FindsRootByName()
    {
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("Main", CreateGroup("Root")));
        schemaSet.Compile();

        var plan = CreateBuilder().Build(schemaSet, new SchemaName("Root"));

        Assert.That(plan.Root.Name, Is.EqualTo("Root"));
    }

    [Test]
    public void Build_FromSchemaSet_QualifiedName_FindsRootInDocument()
    {
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("One", CreateGroup("Root")));
        schemaSet.AddDocument(CreateDocument("Two", CreateGroup("Root")));
        schemaSet.Compile();

        var plan = CreateBuilder().Build(schemaSet, new SchemaName("Two.Root"));

        Assert.That(plan.Root, Is.Not.Null);
    }

    [Test]
    public void Build_FromSchemaSet_UnknownRoot_Throws()
    {
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("Main", CreateGroup("Root")));
        schemaSet.Compile();

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateBuilder().Build(schemaSet, new SchemaName("Missing")));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public void Build_FromSchemaSet_AmbiguousRoot_Throws()
    {
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("One", CreateGroup("Root")));
        schemaSet.AddDocument(CreateDocument("Two", CreateGroup("Root")));
        schemaSet.Compile();

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateBuilder().Build(schemaSet, new SchemaName("Root")));

        Assert.That(ex!.Message, Does.Contain("ambiguous"));
    }

    [Test]
    public void Build_FromSchemaSet_NotCompiled_Throws()
    {
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(CreateDocument("Main", CreateGroup("Root")));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CreateBuilder().Build(schemaSet, new SchemaName("Root")));

        Assert.That(ex!.Message, Does.Contain("not compiled"));
    }

    // ---- helpers ----

    private static ExecutionPlanBuilder CreateBuilder()
    {
        var manager = new ProcessorManager();
        manager.Register(new TestProcessorFactory());
        return new ExecutionPlanBuilder(manager);
    }

    private static SchemaProcessorRef Ref(string id)
        => new() { Processor = new SchemaName($"{Ns}.{id}") };

    private static SchemaGroup CreateGroup(string name, params SchemaProcessorRef[] processors)
        => new() { Name = name, ProcessorsList = [.. processors] };

    private static SchemaField CreateField(string name, params SchemaProcessorRef[] processors)
        => new() { Name = name, Type = SchemaDataType.Int32, ProcessorsList = [.. processors] };

    private static SchemaDocument CreateDocument(string name, SchemaGroup root)
        => new()
        {
            Name = name,
            Roots = [root],
            Groups = [root],
            Fields = [],
            ChildList = [root],
            TypeDefs = [],
            ProcessorDefs = [],
            Includes = []
        };

    private sealed class TestProcessorFactory : IProcessorFactory
    {
        public string Namespace => Ns;

        public IProcessor? CreateProcessor(string id) => id switch
        {
            "value" => new FakeValueProcessor(),
            "layout" => new FakeLayoutProcessor(),
            "bad" => new MismatchedProcessor(),
            _ => null
        };
    }

    private sealed class FakeValueProcessor : IValueProcessor
    {
        public ProcessorKey Key => new(Ns, "value");
        public string Name => "Fake Value";
        public PipelineStage Stage => PipelineStage.Semantic;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public LogicalField Write(LogicalField logicalValue, ValueProcessorContext context) => logicalValue;
        public LogicalField Read(LogicalField logicalValue, ValueProcessorContext context) => logicalValue;
    }

    private sealed class FakeLayoutProcessor : ILayoutProcessor
    {
        public ProcessorKey Key => new(Ns, "layout");
        public string Name => "Fake Layout";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
        public void BeginWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public WriteResult Write(IBufferWriter<byte> writer, EncodedField encodedValue, LayoutProcessorContext context)
            => WriteResult.Success;
        public void EndWrite(IBufferWriter<byte> writer, LayoutProcessorContext context) { }
        public void BeginRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
        public ReadResult Read(ref SequenceReader<byte> reader, out EncodedField outValue, LayoutProcessorContext context)
        {
            outValue = new EncodedField(string.Empty, typeof(object), null, 0);
            return ReadResult.Success;
        }
        public void EndRead(ref SequenceReader<byte> reader, LayoutProcessorContext context) { }
    }

    /// <summary>Declares the Layout stage but does not implement ILayoutProcessor.</summary>
    private sealed class MismatchedProcessor : IProcessor
    {
        public ProcessorKey Key => new(Ns, "bad");
        public string Name => "Mismatched";
        public PipelineStage Stage => PipelineStage.Layout;
        public IReadOnlyList<PropertyDescriptor> Properties => [];
    }
}
