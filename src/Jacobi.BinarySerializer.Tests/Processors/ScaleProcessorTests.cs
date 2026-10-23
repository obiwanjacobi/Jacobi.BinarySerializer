using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class ScaleProcessorTests
{
    private static SchemaGroup CreateRoot(string scale = "0.01")
        => Group("Root", [],
            Field("Temperature", "sys.int32", [Ref("scale", ("scale", scale))]));

    [Test]
    public void RoundTrip_ScalesToAnIntegerOnTheWire_AndBack()
    {
        var (bytes, values) = RoundTrip(CreateRoot(), new() { ["Root.Temperature"] = 12.34m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(1234)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(12.34m));
    }

    [Test]
    public void Read_MultipliesTheWireValueByScale()
    {
        var (bytes, values) = RoundTrip(CreateRoot("0.1"), new() { ["Root.Temperature"] = 10m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(100)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(10m));
    }

    [Test]
    public void RoundTrip_NegativeValue()
    {
        var (bytes, values) = RoundTrip(CreateRoot("0.1"), new() { ["Root.Temperature"] = -5.5m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(-55)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(-5.5m));
    }

    [Test]
    public void RoundTrip_ScaleFromDerivedDataType()
    {
        var def = new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m };
        var manager = new ProcessorManager();
        manager.Register(new Jacobi.BinarySerializer.Processors.ProcessorFactory());
        var dataTypes = Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        dataTypes.RegisterDataTypeDefs([new SchemaDocument { Name = "Doc", DataTypeDefs = [def], NodeDefs = [], ProcessorDefs = [], Includes = [], Roots = [], Groups = [], Fields = [] }]);

        Assert.That(dataTypes.Get(new SchemaName("Doc.Celsius")).Scale, Is.EqualTo(0.01m));
        Assert.That(dataTypes.Get(new SchemaName("Doc.Celsius")).ClrType, Is.EqualTo(dataTypes.Get(new SchemaName("sys.int32")).ClrType));
    }

    [Test]
    public void RoundTrip_FieldUsingDataTypeDef_ScalesViaFacet()
    {
        var (bytes, value) = RoundTripWithDataType(
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m, Processors = [Ref("scale")] },
            [], 12.34m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(1234)));
        Assert.That(value, Is.EqualTo(12.34m));
    }

    [Test]
    public void RoundTrip_ScaleAndShift_ShiftsBeforeScaling()
    {
        var root = Group("Root", [],
            Field("Temperature", "sys.int32", [Ref("scale", ("scale", "0.1"), ("shift", "-40"))]));

        var (bytes, values) = RoundTrip(root, new() { ["Root.Temperature"] = 25.5m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(655)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(25.5m));
    }

    [Test]
    public void RoundTrip_ShiftOnly_UsesScaleOfOne()
    {
        var root = Group("Root", [],
            Field("Year", "sys.int32", [Ref("scale", ("shift", "1900"))]));

        var (bytes, values) = RoundTrip(root, new() { ["Root.Year"] = 2026m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(126)));
        Assert.That(values["Root.Year"], Is.EqualTo(2026m));
    }

    [Test]
    public void Write_ScaleAndShift_RoundsAfterShifting()
    {
        var root = Group("Root", [],
            Field("Temperature", "sys.int32", [Ref("scale", ("scale", "0.1"), ("shift", "0.5"))]));

        var (bytes, values) = RoundTrip(root, new() { ["Root.Temperature"] = 1.27m });

        // (1.27 - 0.5) / 0.1 = 7.7 -> 8; 8 * 0.1 + 0.5 = 1.3
        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(8)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(1.3m));
    }

    [Test]
    public void RoundTrip_FieldUsingDataTypeDef_ScalesAndShiftsViaFacets()
    {
        var (bytes, value) = RoundTripWithDataType(
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.1m, Shift = -40m, Processors = [Ref("scale")] },
            [], 25.5m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(655)));
        Assert.That(value, Is.EqualTo(25.5m));
    }

    [Test]
    public void RoundTrip_FieldUsingDataTypeDef_ShiftOnlyFacet()
    {
        var (bytes, value) = RoundTripWithDataType(
            new SchemaDataTypeDef { Name = "Year", BasedOn = "sys.int32", Shift = 1900m, Processors = [Ref("scale")] },
            [], 2026m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(126)));
        Assert.That(value, Is.EqualTo(2026m));
    }

    [Test]
    public void RoundTrip_ProcessorProperty_IgnoresAllDataTypeFacets()
    {
        // the processor's own scale wins and the facet's shift is not mixed in
        var (bytes, value) = RoundTripWithDataType(
            new SchemaDataTypeDef { Name = "Celsius", BasedOn = "sys.int32", Scale = 0.01m, Shift = 5m },
            [Ref("scale", ("scale", "0.1"))], 12.3m);

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(123)));
        Assert.That(value, Is.EqualTo(12.3m));
    }

    [Test]
    public void Write_MissingScaleProperty_Throws()
    {
        var root = Group("Root", [], Field("Temperature", "sys.int32", [Ref("scale")]));

        Assert.That(() => Write(root, new() { ["Root.Temperature"] = 1m }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    private static (byte[] Bytes, object? Value) RoundTripWithDataType(SchemaDataTypeDef dataTypeDef, SchemaProcessorRef[] fieldProcessors, decimal value)
    {
        var rootGroup = Group("Root", [], new SchemaField { Name = "Temperature", DataType = dataTypeDef.Name, ProcessorsList = [.. fieldProcessors] });
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
            DataTypeDefs = [dataTypeDef]
        };
        var schemaBuilder = new SchemaSetBuilder();
        schemaBuilder.AddDocument(document);
        var schemaSet = schemaBuilder.Compile();

        var manager = new ProcessorManager();
        manager.Register(new Jacobi.BinarySerializer.Processors.ProcessorFactory());
        var dataTypes = Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        var plan = Jacobi.BinarySerializer.Execution.ExecutionPlan.Create(schemaSet, new SchemaName("Doc.Root"), manager, dataTypes);

        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var written = new Jacobi.BinarySerializer.Execution.WriterSession(plan, output)
            .Write(new Jacobi.BinarySerializer.Tests.Execution.DictSource(new() { ["Root.Temperature"] = value }));
        Assert.That(written, Is.EqualTo(WriteResult.Success));

        var sink = new Jacobi.BinarySerializer.Tests.Execution.DictSink();
        var read = new Jacobi.BinarySerializer.Execution.ReaderSession(plan)
            .Read(new System.Buffers.ReadOnlySequence<byte>(output.WrittenMemory), sink);
        Assert.That(read, Is.EqualTo(ReadResult.Success));

        return (output.WrittenSpan.ToArray(), sink.Values["Root.Temperature"]);
    }
}
