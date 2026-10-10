using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class ScaleProcessorTests
{
    private static SchemaGroup CreateRoot(string scale = "100")
        => Group("Root", [],
            Field("Temperature", "Int32", [Ref("scale", ("scale", scale))]));

    [Test]
    public void RoundTrip_ScalesToAnIntegerOnTheWire_AndBack()
    {
        var (bytes, values) = RoundTrip(CreateRoot(), new() { ["Root.Temperature"] = 12.34m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(1234)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(12.34m));
    }

    [Test]
    public void RoundTrip_NegativeValue()
    {
        var (bytes, values) = RoundTrip(CreateRoot("10"), new() { ["Root.Temperature"] = -5.5m });

        Assert.That(bytes, Is.EqualTo(BitConverter.GetBytes(-55)));
        Assert.That(values["Root.Temperature"], Is.EqualTo(-5.5m));
    }

    [Test]
    public void RoundTrip_ScaleFromDerivedDataType()
    {
        var def = new SchemaDataTypeDef { Name = "Celsius", BasedOn = "Int32", Scale = 100m };
        var manager = new ProcessorManager();
        manager.Register(new Jacobi.BinarySerializer.Processors.ProcessorFactory());
        var dataTypes = Jacobi.BinarySerializer.Descriptors.DataTypeRegistry.CreateDefault();
        foreach (var dataType in manager.DataTypes)
        {
            dataTypes.Register(dataType);
        }
        dataTypes.RegisterDataTypeDefs([new SchemaDocument { Name = "Doc", DataTypeDefs = [def], NodeDefs = [], ProcessorDefs = [], Includes = [], Roots = [], Groups = [], Fields = [] }]);

        Assert.That(dataTypes.Get(new SchemaName("Doc.Celsius")).Scale, Is.EqualTo(100m));
        Assert.That(dataTypes.Get(new SchemaName("Doc.Celsius")).ClrType, Is.EqualTo(dataTypes.Get(new SchemaName("sys.int32")).ClrType));
    }

    [Test]
    public void RoundTrip_FieldUsingDataTypeDef_ScalesViaFacet()
    {
        var rootGroup = Group("Root", [], new SchemaField { Name = "Temperature", DataType = "Celsius", ProcessorsList = [] });
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
            DataTypeDefs = [new SchemaDataTypeDef { Name = "Celsius", BasedOn = "Int32", Scale = 100m, Processors = [Ref("scale")] }]
        };
        var schemaSet = new SchemaSet();
        schemaSet.AddDocument(document);
        schemaSet.Compile();

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
            .Write(new Jacobi.BinarySerializer.Tests.Execution.DictSource(new() { ["Root.Temperature"] = 12.34m }));
        Assert.That(written, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(BitConverter.GetBytes(1234)));

        var sink = new Jacobi.BinarySerializer.Tests.Execution.DictSink();
        var read = new Jacobi.BinarySerializer.Execution.ReaderSession(plan)
            .Read(new System.Buffers.ReadOnlySequence<byte>(output.WrittenMemory), sink);
        Assert.That(read, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.Temperature"], Is.EqualTo(12.34m));
    }

    [Test]
    public void Write_MissingScaleProperty_Throws()
    {
        var root = Group("Root", [], Field("Temperature", "Int32", [Ref("scale")]));

        Assert.That(() => Write(root, new() { ["Root.Temperature"] = 1m }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }
}
