using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.IntegrationTests.Png;

public class PngSchemaTests
{
    [Test]
    public void Schema_LoadsCompilesAndBuildsPlan()
    {
        var schemaSet = new SchemaSet();
        schemaSet.LoadFile(TestFiles.Path("Png/png.json"));
        schemaSet.Compile();

        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());

        var serializer = new SerializerBuilder()
            .AddSchemas(schemaSet)
            .AddProcessors(manager)
            .Build();

        var plan = serializer.GetPlan(new SchemaName("Png.Png"));

        Assert.That(plan, Is.Not.Null);
    }

    [Test]
    public void Read_Grayscale8_ReadsEveryChunk()
    {
        var schemaSet = new SchemaSet();
        schemaSet.LoadFile(TestFiles.Path("Png/png.json"));
        schemaSet.Compile();
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        var serializer = new SerializerBuilder().AddSchemas(schemaSet).AddProcessors(manager).Build();
        var plan = serializer.GetPlan(new SchemaName("Png.Png"));
        var bytes = PngBuilder.Grayscale8(2, 2);
        var sink = new Sink();

        var result = new Execution.ReaderSession(plan, valueFieldsUseModel: true).Read(new System.Buffers.ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success), $"read {sink.Seen.Count}: {string.Join(", ", sink.Seen)}; bytes {bytes.Length}");
        Assert.That(sink.Types, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));
        Assert.That(sink.Values["Png.Signature"], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.That(((byte[])sink.Values["Png.Chunk.Body.Data.ImageData"]!).Length, Is.GreaterThan(0));
    }

    [Test]
    public void RoundTrip_Grayscale8_WritesTheSameBytes()
    {
        var schemaSet = new SchemaSet();
        schemaSet.LoadFile(TestFiles.Path("Png/png.json"));
        schemaSet.Compile();
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        var serializer = new SerializerBuilder().AddSchemas(schemaSet).AddProcessors(manager).Build();
        var plan = serializer.GetPlan(new SchemaName("Png.Png"));
        var bytes = PngBuilder.Grayscale8(2, 2);
        var recorder = new Recorder();
        var readResult = new Execution.ReaderSession(plan).Read(new System.Buffers.ReadOnlySequence<byte>(bytes), recorder);
        Assert.That(readResult, Is.EqualTo(ReadResult.Success));

        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writeResult = new Execution.WriterSession(plan, output).Write(recorder);

        Assert.That(writeResult, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(bytes));
    }

    [Test]
    public void RoundTrip_UnknownChunk_KeepsTypeAndBytes()
    {
        var schemaSet = new SchemaSet();
        schemaSet.LoadFile(TestFiles.Path("Png/png.json"));
        schemaSet.Compile();
        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        var serializer = new SerializerBuilder().AddSchemas(schemaSet).AddProcessors(manager).Build();
        var plan = serializer.GetPlan(new SchemaName("Png.Png"));
        using var ms = new MemoryStream();
        ms.Write(PngBuilder.Signature);
        PngBuilder.WriteChunk(ms, "tEXt", "Comment\0hello"u8);
        PngBuilder.WriteChunk(ms, "IEND", []);
        var bytes = ms.ToArray();
        var recorder = new Recorder();

        var readResult = new Execution.ReaderSession(plan).Read(new System.Buffers.ReadOnlySequence<byte>(bytes), recorder);
        Assert.That(readResult, Is.EqualTo(ReadResult.Success));

        var output = new System.Buffers.ArrayBufferWriter<byte>();
        var writeResult = new Execution.WriterSession(plan, output).Write(recorder);

        Assert.That(writeResult, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(bytes));
    }

    private sealed class Recorder : IFieldSink, IFieldSource
    {
        private readonly Dictionary<string, LogicalField> _values = [];

        public void SetField(FieldContext context, LogicalField value) => _values[$"{context.Path}{context.Instance}"] = value;

        public SourceResult GetField(FieldContext context)
        {
            if (_values.TryGetValue($"{context.Path}{context.Instance}", out var value))
            {
                return SourceResult.Provided(value);
            }
            return context.Path == "Png.Chunk.Length" ? SourceResult.EndOfData() : SourceResult.NoValue();
        }
    }

    private sealed class Sink : IFieldSink
    {
        public List<string> Types { get; } = [];
        public List<string> Seen { get; } = [];
        public Dictionary<string, object?> Values { get; } = [];

        public void SetField(FieldContext context, LogicalField value)
        {
            Values[context.Path] = value.Value;
            Seen.Add($"{context.Path}={value.Value}");
            if (context.Name == "Type")
            {
                Types.Add((string)value.Value!);
            }
        }
    }
}
