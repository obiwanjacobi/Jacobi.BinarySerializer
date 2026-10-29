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
    private static readonly Dictionary<string, string> Colors = new() { ["1"] = "Red", ["2"] = "Green" };

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
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "Base", Max = "100" },
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
    public void Write_OptionsFacet_PhysicalValue_Throws()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors });

        // the value model provides logical values (names) only
        Assert.That(() => Write(plan, 1), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("options"));
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
    public void RoundTrip_OptionsFacet_WithValueType_MapsIntToInt()
    {
        var plan = Build(new SchemaDataTypeDef
        {
            Name = "Code", BasedOn = "sys.uint8", ValueType = "sys.int32",
            Options = new Dictionary<string, string> { ["1"] = "100", ["2"] = "200" },
        });

        var (bytes, value) = RoundTrip(plan, 200);

        Assert.That(bytes, Is.EqualTo(new byte[] { 2 }));
        Assert.That(value, Is.EqualTo(200));
        // the logical value is matched, not the physical value
        Assert.That(() => Write(plan, 1), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("options"));
    }

    [Test]
    public void Read_OptionsFacet_UnknownPhysicalValue_ReadsTheDefault()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors, OptionsDefault = "Unknown" });

        Assert.That(Read(plan, [3]), Is.EqualTo("Unknown"));
        Assert.That(Read(plan, [1]), Is.EqualTo("Red"));
        // the default has no physical value
        Assert.That(() => Write(plan, "Unknown"), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("options"));
    }

    [Test]
    public void Read_OptionsFacet_DefaultOfTheValueType()
    {
        var plan = Build(new SchemaDataTypeDef
        {
            Name = "Code", BasedOn = "sys.uint8", ValueType = "sys.int32", OptionsDefault = "-1",
            Options = new Dictionary<string, string> { ["1"] = "100" },
        });

        Assert.That(Read(plan, [7]), Is.EqualTo(-1));
    }

    [Test]
    public void OptionsDefault_IsInheritedWithTheOptions()
    {
        var plan = Build(
            new SchemaDataTypeDef { Name = "Color", BasedOn = "Base" },
            new SchemaDataTypeDef { Name = "Base", BasedOn = "sys.uint8", Options = Colors, OptionsDefault = "Unknown" });

        Assert.That(Read(plan, [3]), Is.EqualTo("Unknown"));
    }

    [TestCase("x", "Red", null, null)]
    [TestCase("1", "Red", "sys.int32", null)]
    [TestCase("1", "100", "sys.int32", "none")]
    [TestCase("1", "100", "sys.unknown", null)]
    public void InvalidOptions_Throw(string physical, string logical, string? valueType, string? optionsDefault)
    {
        var def = new SchemaDataTypeDef
        {
            Name = "Code", BasedOn = "sys.uint8",
            ValueType = valueType is null ? (SchemaName?)null : new SchemaName(valueType), OptionsDefault = optionsDefault,
            Options = new Dictionary<string, string> { [physical] = logical },
        };

        Assert.That(() => Build(def), Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void OptionsDefault_WithoutOptions_Throws()
    {
        Assert.That(() => Build(new SchemaDataTypeDef { Name = "Code", BasedOn = "sys.uint8", OptionsDefault = "x" }),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void ValueType_WithoutFacets_ConvertsAtTheModelBoundary()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Code", BasedOn = "sys.uint8", ValueType = "sys.int32" });

        var (bytes, value) = RoundTrip(plan, 7L);

        Assert.That(bytes, Is.EqualTo(new byte[] { 7 }));
        Assert.That(value, Is.EqualTo(7));
        Assert.That(plan.Root.Members.OfType<FieldInfo>().Single().ValueType.ClrType, Is.EqualTo(typeof(int)));
    }

    [Test]
    public void ValueType_OfAScaledType_ConvertsTheScaledValue()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int16", Scale = 0.5m, ValueType = "sys.float32" });

        var (bytes, value) = RoundTrip(plan, 2.5);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes((short)5)));
        Assert.That(value, Is.EqualTo(2.5));
    }

    [TestCase("Color", typeof(string))]
    [TestCase("Celsius", typeof(decimal))]
    [TestCase("Percent", typeof(byte))]
    public void ValueType_IsImpliedByTheFacets(string name, Type expected)
    {
        var plan = Build(
            // a def without facets of its own inherits the value type
            new SchemaDataTypeDef { Name = "Field", BasedOn = name },
            new SchemaDataTypeDef { Name = "Percent", BasedOn = "sys.uint8" },
            new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors },
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.uint8", Scale = 0.5m });

        Assert.That(plan.Root.Members.OfType<FieldInfo>().Single().ValueType.ClrType, Is.EqualTo(expected));
    }

    [Test]
    public void InheritedOptions_AreConvertedToTheValueType()
    {
        var plan = Build(
            new SchemaDataTypeDef { Name = "Number", BasedOn = "Base", ValueType = "sys.int64" },
            new SchemaDataTypeDef { Name = "Base", BasedOn = "sys.uint8", ValueType = "sys.int32",
                Options = new Dictionary<string, string> { ["1"] = "100" }, OptionsDefault = "-1" });

        var (bytes, value) = RoundTrip(plan, 100L);

        Assert.That(bytes, Is.EqualTo(new byte[] { 1 }));
        Assert.That(value, Is.EqualTo(100L));
        Assert.That(Read(plan, [9]), Is.EqualTo(-1L));
    }

    [Test]
    public void DeclaredSemanticProcessor_ValueTypeIsTheDataType()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m, Processors = [Ref("scale", ("scale", "0.1"))] });

        var field = plan.Root.Members.OfType<FieldInfo>().Single();
        Assert.That(field.ValueType, Is.SameAs(field.DataType));
    }

    [Test]
    public void Constant_WithAnExplicitValueType_IsTheLogicalValue()
    {
        var plan = Build([Field("Temperature", "Code", "200")],
            new SchemaDataTypeDef
            {
                Name = "Code", BasedOn = "sys.uint8", ValueType = "sys.int32",
                Options = new Dictionary<string, string> { ["1"] = "100", ["2"] = "200" },
            });

        Assert.That(plan.Root.Members.OfType<FieldInfo>().Single().ConstantValue, Is.EqualTo(200));
        Assert.That(Write(plan, []), Is.EqualTo(new byte[] { 2 }));
        Assert.That(ReadAll(plan, [2]), Is.Empty);
        Assert.That(() => ReadAll(plan, [1]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("200"));
    }

    [Test]
    public void FieldRefValueType_IsTheValueTypeOfTheReferencedField()
    {
        var plan = Build([Field("Type", "Kind"), Choice(Ref("map", ("to", "sys.int32"), ("Header", "0"), ("Palette", "1")))],
            new SchemaDataTypeDef { Name = "Kind", BasedOn = "sys.uint8", Options = new Dictionary<string, string> { ["1"] = "Header", ["2"] = "Palette" } });

        var choice = plan.Root.Members.OfType<ChoiceInfo>().Single();
        Assert.That(choice.FieldRefValueType!.ClrType, Is.EqualTo(typeof(string)));
    }

    [Test]
    public void ChoiceIndex_MappedFromTheLogicalValueOfAnOptionsField()
    {
        // the map receives the published (logical) value: its keys are option values, not physical values
        var plan = Build([Field("Type", "Kind"), Choice(Ref("map", ("to", "sys.int32"), ("Header", "0"), ("Palette", "1")))],
            new SchemaDataTypeDef { Name = "Kind", BasedOn = "sys.uint8", Options = new Dictionary<string, string> { ["1"] = "Header", ["2"] = "Palette" } });

        var bytes = Write(plan, new() { ["Root.Type"] = "Palette", ["Root.Pick.B"] = (short)7 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 7, 0 }));
        var values = ReadAll(plan, bytes);
        Assert.That(values["Root.Type"], Is.EqualTo("Palette"));
        Assert.That(values["Root.Pick.B"], Is.EqualTo((short)7));
    }

    [Test]
    public void ChoiceIndex_FacetsOfTheReferencedField_AreNotAppliedTwice()
    {
        // the published value is already scaled (physical 2 -> 1.0); scaling it again would select index 0
        var plan = Build([Field("Type", "Half"), Choice(Ref("datatype"))],
            new SchemaDataTypeDef { Name = "Half", BasedOn = "sys.uint8", Scale = 0.5m });

        var values = ReadAll(plan, [2, 7, 0]);

        Assert.That(values["Root.Type"], Is.EqualTo(1.0m));
        Assert.That(values["Root.Pick.B"], Is.EqualTo((short)7));
        Assert.That(values, Does.Not.ContainKey("Root.Pick.A"));
    }

    [Test]
    public void Constant_OfAnOptionsField_IsTheLogicalValue()
    {
        var plan = Build([Field("Temperature", "Color", "Green")],
            new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors });

        Assert.That(Write(plan, []), Is.EqualTo(new byte[] { 2 }));
        Assert.That(ReadAll(plan, [2]), Is.Empty);
        Assert.That(() => ReadAll(plan, [1]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("Green"));
    }

    [Test]
    public void Constant_OfAScaledField_IsTheLogicalValue()
    {
        var plan = Build([Field("Temperature", "Celsius", "12.34")],
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m });

        Assert.That(Write(plan, []), Is.EqualTo(BitConverter.GetBytes(1234)));
        Assert.That(ReadAll(plan, BitConverter.GetBytes(1234)), Is.Empty);
    }

    [Test]
    public void ReferencedValue_IsTheLogicalValue_OnWriteAndRead()
    {
        var code = new SchemaDataTypeDef
        {
            Name = "Code", BasedOn = "sys.uint8", ValueType = "sys.int32",
            Options = new Dictionary<string, string> { ["1"] = "100", ["2"] = "200" },
        };
        var plan = Build([Field("Temperature", "Code"), Field("Copy", "Code", new SchemaNodeRef { Path = "Root.Temperature" })], code);

        var bytes = Write(plan, new() { ["Root.Temperature"] = 200 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 2 }));
        Assert.That(ReadAll(plan, bytes)["Root.Temperature"], Is.EqualTo(200));
    }

    [Test]
    public void MinMaxFacets_WithOptions_ValidateTheOptionValue()
    {
        var plan = Build(new SchemaDataTypeDef
        {
            Name = "Code", BasedOn = "sys.uint8", ValueType = "sys.int32", Max = "150",
            Options = new Dictionary<string, string> { ["1"] = "100", ["2"] = "200" },
        });

        Assert.That(Write(plan, 100), Is.EqualTo(new byte[] { 1 }));
        Assert.That(() => Write(plan, 200), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
        Assert.That(() => Read(plan, [2]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
    }

    [Test]
    public void MinMaxFacets_ValidateTheScaledValue()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.uint8", Scale = 0.5m, Max = "10" });

        Assert.That(Write(plan, 10m), Is.EqualTo(new byte[] { 20 }));
        Assert.That(() => Write(plan, 10.5m), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
        Assert.That(() => Read(plan, [21]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
    }

    [Test]
    public void MinMaxFacets_WithStrings_CompareOrdinal()
    {
        // 'Green' < 'H' < 'Red'
        var plan = Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Options = Colors, Max = "H" });

        Assert.That(Write(plan, "Green"), Is.EqualTo(new byte[] { 2 }));
        Assert.That(() => Write(plan, "Red"), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
        Assert.That(() => Read(plan, [1]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
    }

    [Test]
    public void MinMaxFacets_OnAStringField()
    {
        var plan = Build([new SchemaField { Name = "Temperature", DataType = "Code", ByteLength = 2 }],
            new SchemaDataTypeDef { Name = "Code", BasedOn = "sys.string", Min = "AA", Max = "MZ", Processors = [Ref("string")] });

        Assert.That(Write(plan, "KL"), Is.EqualTo("KL"u8.ToArray()));
        Assert.That(() => Write(plan, "NA"), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
    }

    [TestCase("sys.bytes", "00", null, "compared")]
    [TestCase("sys.int32", "x", null, "not a valid")]
    [TestCase("sys.int32", "10", "5", "larger")]
    public void MinMaxFacets_Invalid_Throw(string valueType, string? min, string? max, string message)
    {
        Assert.That(() => Build(new SchemaDataTypeDef { Name = "Code", BasedOn = "sys.uint8", ValueType = valueType, Min = min, Max = max }),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contain(message));
    }

    [Test]
    public void MinMaxFacets_ValidateTheLogicalValue()
    {
        var plan = Build(new SchemaDataTypeDef { Name = "Percent", BasedOn = "sys.uint8", Min = "0", Max = "100" });

        var (bytes, value) = RoundTrip(plan, (byte)100);

        Assert.That(bytes, Is.EqualTo(new byte[] { 100 }));
        Assert.That(value, Is.EqualTo((byte)100));
        Assert.That(() => Write(plan, (byte)101), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
        Assert.That(() => Read(plan, [101]), Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("range"));
    }

    [Test]
    public void OptionsWithScale_ThrowsAtPlanBuild()
    {
        Assert.That(() => Build(new SchemaDataTypeDef { Name = "Color", BasedOn = "sys.uint8", Scale = 2m, Options = Colors }),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contain("cannot combine options"));
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
        => Compile([new SchemaField { Name = "Temperature", DataType = dataTypeDefs[0].Name }], dataTypeDefs);

    private static SchemaSet Compile(SchemaNode[] nodes, SchemaDataTypeDef[] dataTypeDefs)
    {
        var rootGroup = Group("Root", [], nodes);
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
        => Build([new SchemaField { Name = "Temperature", DataType = dataTypeDefs[0].Name }], dataTypeDefs);

    private static ExecutionPlan Build(SchemaNode[] nodes, params SchemaDataTypeDef[] dataTypeDefs)
    {
        var manager = new ProcessorManager();
        manager.Register(new Jacobi.BinarySerializer.Processors.ProcessorFactory());
        var dataTypes = DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        return ExecutionPlan.Create(Compile(nodes, dataTypeDefs), new SchemaName("Doc.Root"), manager, dataTypes);
    }

    // a choice of two int16 alternatives, selected by the value of the field 'Root.Type'
    private static SchemaChoice Choice(params SchemaProcessorRef[] valueProcessors)
    {
        var choice = new SchemaChoice
        {
            Name = "Pick",
            SelectedIndex = new SchemaNodeRef { Path = "Root.Type" },
            ValueProcessorsList = [.. valueProcessors],
        };
        choice.MemberList.Add(Field("A", "sys.int16"));
        choice.MemberList.Add(Field("B", "sys.int16"));
        return choice;
    }

    private static SchemaField Field(string name, string dataType, SchemaValueOrRef<string> value = default)
        => new() { Name = name, DataType = dataType, Value = value };

    private static byte[] Write(ExecutionPlan plan, Dictionary<string, object?> values)
    {
        var output = new ArrayBufferWriter<byte>();
        var written = new WriterSession(plan, output).Write(new DictSource(values));
        Assert.That(written, Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    private static Dictionary<string, object?> ReadAll(ExecutionPlan plan, byte[] bytes)
    {
        var sink = new DictSink();
        var read = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);
        Assert.That(read, Is.EqualTo(ReadResult.Success));
        return sink.Values;
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
