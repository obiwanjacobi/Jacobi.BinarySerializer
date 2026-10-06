using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>A repeat without a count runs until the end of the input.</summary>
public class SessionRepeatUntilEndTests
{
    private static SchemaRepeat Items()
    {
        var repeat = new SchemaRepeat { Name = "Items" };
        repeat.ChildList.Add(Field("Byte", SchemaDataType.UInt8));
        return repeat;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    public void Read_RepeatWithoutCount_ReadsUntilEnd(int items)
    {
        var plan = Build(Group("Root", [], Field("Head", SchemaDataType.UInt8), Items()));
        var bytes = new byte[1 + items];
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Count, Is.EqualTo(1 + items));
    }

    [Test]
    public void Build_RepeatWithoutCountNotLast_ReportsError()
    {
        var root = Group("Root", [], Items(), Field("Tail", SchemaDataType.UInt8));

        var ex = Assert.Throws<ExecutionPlanException>(() => Build(root));

        Assert.That(ex!.Message, Does.Contain("Root.Items").And.Contain("last"));
    }

    private sealed class Sink : IFieldSink
    {
        public int Count { get; private set; }
        public void SetField(FieldContext context, LogicalField value) => Count++;
    }
}
