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
        repeat.MemberList.Add(Field("Byte", "sys.uint8"));
        return repeat;
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(4)]
    public void Read_RepeatWithoutCount_ReadsUntilEnd(int items)
    {
        var plan = Build(Group("Root", [], Field("Head", "sys.uint8"), Items()));
        var bytes = new byte[1 + items];
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(bytes), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Count, Is.EqualTo(1 + items));
    }

    [Test]
    public void Build_RepeatWithoutCountNotLast_ReportsError()
    {
        var root = Group("Root", [], Items(), Field("Tail", "sys.uint8"));

        var ex = Assert.Throws<ExecutionPlanException>(() => Build(root));

        Assert.That(ex!.Message, Does.Contain("Root.Items").And.Contain("last"));
    }

    [Test]
    public void Read_OpenRepeatAsRoot_ReadsUntilEnd()
    {
        var plan = Build(Items());
        var sink = new Sink();

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Count, Is.EqualTo(3));
    }

    [Test]
    public void Read_ChoiceAsRoot_ReadsTheSelectedAlternative()
    {
        var choice = new SchemaChoice { Name = "Root", SelectedIndex = 1 };
        choice.MemberList.Add(Field("A", "sys.uint8"));
        choice.MemberList.Add(Field("B", "sys.uint16"));
        var sink = new Sink();

        var result = new ReaderSession(Build(choice)).Read(new ReadOnlySequence<byte>(new byte[] { 1, 0 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Count, Is.EqualTo(1));
    }

    [Test]
    public void Build_ChoiceWithOpenAlternativeNotLast_ReportsError()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = 0 };
        choice.MemberList.Add(Items());
        choice.MemberList.Add(Field("Other", "sys.uint8"));
        var root = Group("Root", [], choice, Field("Tail", "sys.uint8"));

        var ex = Assert.Throws<ExecutionPlanException>(() => Build(root));

        Assert.That(ex!.Message, Does.Contain("Root.Pick"));
    }

    [Test]
    public void Build_ChoiceOfOpenAlternativesLast_Succeeds()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = 0 };
        choice.MemberList.Add(Items());
        choice.MemberList.Add(Items());

        Assert.That(() => Build(Group("Root", [], Field("Head", "sys.uint8"), choice)), Throws.Nothing);
    }

    [Test]
    public void Build_SizedChoiceWithOpenAlternativeNotLast_Succeeds()
    {
        var choice = new SchemaChoice { Name = "Pick", SelectedIndex = 0, ByteSize = 2 };
        choice.MemberList.Add(Items());
        choice.MemberList.Add(Items());

        Assert.That(() => Build(Group("Root", [], choice, Field("Tail", "sys.uint8"))), Throws.Nothing);
    }

    [Test]
    public void Write_OpenRepeatFromFlatSource_StopsAtEndOfData()
    {
        var plan = Build(Group("Root", [], Field("Head", "sys.uint8"), Items()));
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(plan, output).Write(new ItemsSource(3));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void Write_EndOfDataOutsideOpenRepeat_Throws()
    {
        var plan = Build(Group("Root", [], Field("Head", "sys.uint8")));

        Assert.That(() => new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(new ItemsSource(-1)), Throws.Exception);
    }

    private sealed class ItemsSource(int items) : IFieldSource
    {
        public SourceResult GetField(FieldContext context)
        {
            if (context.Path.ToString() == "Root.Head")
            {
                return items < 0 ? SourceResult.EndOfData() : SourceResult.Provided(new LogicalField("Head", typeof(byte), (byte)0));
            }
            if (context.Path.ToString() == "Root.Items.Byte")
            {
                var index = context.Instance.ToString().Trim('[', ']').Split('[', ']').Where(s => s.Length > 0).Select(Int32.Parse).Last();
                if (index >= items)
                {
                    return SourceResult.EndOfData();
                }
                return SourceResult.Provided(new LogicalField("Byte", typeof(byte), (byte)(index + 1)));
            }
            return SourceResult.NoValue();
        }
    }

    private sealed class Sink : IFieldSink
    {
        public int Count { get; private set; }
        public void SetField(FieldContext context, LogicalField value) => Count++;
    }
}
