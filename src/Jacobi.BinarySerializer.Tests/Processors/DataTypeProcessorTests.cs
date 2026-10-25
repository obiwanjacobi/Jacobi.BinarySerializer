using System.Buffers;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class DataTypeProcessorTests
{
    private static readonly Dictionary<long, string> Colors = new() { [1] = "Red", [2] = "Green" };

    [Test]
    public void RoundTrip_ScaleFacet_WithoutProcessors()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m });

        var (bytes, value) = RoundTrip(plan, 12.34m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(1234)));
        Assert.That(value, Is.EqualTo(12.34m));
    }

    [Test]
    public void RoundTrip_ScaleAndShiftFacets_WithoutProcessors()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.1m, Shift = -40m });

        var (bytes, value) = RoundTrip(plan, 25.5m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(655)));
        Assert.That(value, Is.EqualTo(25.5m));
    }

    [Test]
    public void RoundTrip_FacetsOfBaseDef_AreInherited()
    {
        var plan = Build(
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "Base", Max = 100m },
            new SchemaDataTypeDef { Name = "Base", BasedOn = "sys.int32", Scale = 0.1m });

        var (bytes, value) = RoundTrip(plan, 25.5m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(255)));
        Assert.That(value, Is.EqualTo(25.5m));
        Assert.That(() => Write(plan, 100.1m), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void RoundTrip_OptionsFacet_WritesNameAsPhysicalValue_AndReadsName()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors });

        var (bytes, value) = RoundTrip(plan, "Green");

        Assert.That(bytes, Is.EqualTo(new byte[] { 2 }));
        Assert.That(value, Is.EqualTo("Green"));
    }

    [Test]
    public void Write_OptionsFacet_AcceptsAnOptionValue()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors });

        Assert.That(Write(plan, 1), Is.EqualTo(new byte[] { 1 }));
    }

    [Test]
    public void Write_OptionsFacet_UnknownNameOrValue_Throws()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors });

        Assert.That(() => Write(plan, "Blue"), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("Blue"));
        Assert.That(() => Write(plan, 3), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Read_OptionsFacet_UnknownPhysicalValue_Throws()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors });

        Assert.That(() => Read(plan, [3]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("options"));
    }

    [Test]
    public void MinMaxFacets_ValidateTheLogicalValue()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Percent", BasedOn = "sys.uint8", Min = 0m, Max = 100m });

        var (bytes, value) = RoundTrip(plan, (byte)100);

        Assert.That(bytes, Is.EqualTo(new byte[] { 100 }));
        Assert.That(value, Is.EqualTo((byte)100));
        Assert.That(() => Write(plan, (byte)101), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
        Assert.That(() => Read(plan, [101]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
    }

    [Test]
    public void OptionsWithScale_Throws()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Scale = 2m, Options = Colors });

        Assert.That(() => Write(plan, "Red"), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("options"));
    }

    [Test]
    public void DeclaredSemanticProcessor_ReplacesTheDefault()
    {
        // the default would scale by 0.01 as well; only the declared sys.scale may apply
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m, Processors = [Ref("scale", ("scale", "0.1"))] });

        var (bytes, value) = RoundTrip(plan, 12.3m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(123)));
        Assert.That(value, Is.EqualTo(12.3m));
        Assert.That(plan.Root.Members.OfType<FieldInfo>().Single().Pipeline.ValueProcessors.Select(p => p.Processor.Key.ToString()),
            Is.EqualTo(new[] { "sys.scale" }));
    }

    [Test]
    public void DeclaredFieldProcessor_KeepsTheDefault()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.uint32", Scale = 0.1m, Processors = [Ref("varint")] });

        var (bytes, value) = RoundTrip(plan, 30m);

        // 300 as leb128
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAC, 0x02 }));
        Assert.That(value, Is.EqualTo(30m));
    }

    [Test]
    public void DataTypeWithoutFacets_HasNoValueProcessor()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Word", BasedOn = "sys.uint16" });

        Assert.That(plan.Root.Members.OfType<FieldInfo>().Single().Pipeline.ValueProcessors, Is.Empty);
    }

    [Test]
    public void Facets_WithoutSysProcessors_ReportsPlanError()
    {
        var def = new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m };

        var ex = Assert.Throws<ExecutionPlanException>(() => ExecutionPlan.Create(Compile(def), new SchemaName("Doc.Root"), new ProcessorManager(), DataTypeRegistry.CreateDefault()));

        Assert.That(ex!.Errors[0], Does.Contain("Root.Temperature").And.Contain("sys.datatype"));
    }

    private static SchemaSet Compile(params SchemaDataTypeDef[] dataTypeDefs)
    {
        var rootGroup = Group("Root", [], new SchemaField { Name = "Temperature", DataType = dataTypeDefs[0].Name });
        var document = new SchemaDocument
        {
            Name = "Doc",
            Roots = [rootGroup],
            Groups = [rootGroup],
            Fields = [],
            MemberList = [rootGroup],
            NodeDefs = [],
            ProcessorDefs = [],
            Includes = [],
            DataTypeDefs = dataTypeDefs
        };
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(document);
        return schemaBuilder.Compile();
    }

    private static ExecutionPlan Build(params SchemaDataTypeDef[] dataTypeDefs)
    {
        var manager = new ProcessorManager();
        manager.Register(new Jacobi.BinarySerializer.Processors.ProcessorFactory());
        var dataTypes = DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        return ExecutionPlan.Create(Compile(dataTypeDefs), new SchemaName("Doc.Root"), manager, dataTypes);
    }

    private static byte[] Write(ExecutionPlan plan, object value)
    {
        var output = new ArrayBufferWriter<byte>();
        var written = new WriterSession(plan, output).Write(new DictSource(new() { ["Root.Temperature"] = value }));
        Assert.That(written, Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    private static object? Read(ExecutionPlan plan, byte[] bytes)
    {
        var sink = new DictSink();
        var read = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);
        Assert.That(read, Is.EqualTo(ReadResult.Success));
        return sink.Values["Root.Temperature"];
    }

    private static (byte[] Bytes, object? Value) RoundTrip(ExecutionPlan plan, object value)
    {
        var bytes = Write(plan, value);
        return (bytes, Read(plan, bytes));
    }
}
