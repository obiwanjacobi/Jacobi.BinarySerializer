using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

/// <summary>Value processors on the count of a repeat and the selected index of a choice.</summary>
public class ValueProcessorParameterTests
{
    private static SchemaGroup ChoiceRoot(params SchemaProcessorRef[] valueProcessors)
    {
        var choice = new SchemaChoice
        {
            Name = "Pick",
            SelectedIndex = new SchemaNodeRef { Path = "Root.Type" },
            ValueProcessorsList = [.. valueProcessors],
        };
        choice.MemberList.Add(Field("A", "Int16"));
        choice.MemberList.Add(Field("B", "Int16"));

        return Group("Root", [],
            Field("Type", "String", [Ref("string", ("byteLength", "4"), ("encoding", "ascii"))]),
            choice);
    }

    private static SchemaProcessorRef TypeMap()
        => Ref("map", ("logical", "Int32"), ("0", "IHDR"), ("1", "PLTE"));

    [TestCase("IHDR", "A", 5)]
    [TestCase("PLTE", "B", 7)]
    public void RoundTrip_ChoiceIndexMappedFromString_SelectsAlternative(string type, string alternative, int number)
    {
        var plan = Build(ChoiceRoot(TypeMap()));
        var output = new ArrayBufferWriter<byte>();
        var source = new Source(new() { ["Root.Type"] = type, [$"Root.Pick.{alternative}"] = (short)number });

        Assert.That(new WriterSession(plan, output).Write(source), Is.EqualTo(WriteResult.Success));
        Assert.That(System.Text.Encoding.ASCII.GetString(output.WrittenSpan[..4]), Is.EqualTo(type));
        Assert.That(output.WrittenCount, Is.EqualTo(6));

        var sink = new Sink();
        var read = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink);

        Assert.That(read, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.Type"], Is.EqualTo(type));
        Assert.That(sink.Values[$"Root.Pick.{alternative}"], Is.EqualTo((short)number));
    }

    private sealed class Source(Dictionary<string, object?> values) : IFieldSource
    {
        public SourceResult GetField(FieldContext context) => TryGetField(context, out var found) ? SourceResult.Provided(found) : SourceResult.NoValue();
        private bool TryGetField(FieldContext context, [NotNullWhen(true)] out LogicalField? field)
        {
            if (values.TryGetValue(context.Path, out var value))
            {
                field = new LogicalField(context.Name, value!.GetType(), value);
                return true;
            }
            field = null;
            return false;
        }
    }

    private sealed class Sink : IFieldSink
    {
        public Dictionary<string, object?> Values { get; } = [];
        public void SetField(FieldContext context, LogicalField value) => Values[context.Path] = value.Value;
    }

    [Test]
    public void Write_ChoiceIndexUnmapped_Throws()
    {
        Assert.That(() => Write(ChoiceRoot(TypeMap()), new() { ["Root.Type"] = "IDAT" }, out _),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void Build_NonSemanticValueProcessor_ReportsError()
    {
        var root = ChoiceRoot(Ref("string", ("byteLength", "4")));

        var ex = Assert.Throws<ExecutionPlanException>(() => Build(root));

        Assert.That(ex!.Message, Does.Contain("Root.Pick").And.Contain("value (semantic) processor"));
    }
}
