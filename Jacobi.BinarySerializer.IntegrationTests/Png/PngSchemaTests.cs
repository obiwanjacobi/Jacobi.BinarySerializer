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

        var result = new Execution.ReaderSession(plan).Read(new System.Buffers.ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success), $"read {sink.Seen.Count}: {string.Join(", ", sink.Seen)}; bytes {bytes.Length}");
        Assert.That(sink.Types, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }));
    }

    private sealed class Sink : IFieldSink
    {
        public List<string> Types { get; } = [];
        public List<string> Seen { get; } = [];

        public void SetField(FieldContext context, LogicalField value)
        {
            Seen.Add($"{context.Path}={value.Value}");
            if (context.Name == "Type")
            {
                Types.Add((string)value.Value!);
            }
        }
    }
}
