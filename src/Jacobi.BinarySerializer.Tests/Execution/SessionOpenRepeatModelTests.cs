using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>A value model that implements IOpenRepeatSink/IOpenRepeatSource decides when an open repeat ends.</summary>
public class SessionOpenRepeatModelTests
{
    private static SchemaGroup Root()
    {
        var repeat = new SchemaRepeat { Name = "Items" };
        repeat.MemberList.Add(Field("Byte", "sys.uint8"));
        return Group("Root", [], Field("Head", "sys.uint8"), repeat);
    }

    [Test]
    public void Read_SinkEndsAfterTerminatingItem_StopsBeforeRemainingInput()
    {
        // stop after an item with value 0 (the terminator is part of the list)
        var sink = new Sink((context, items) => items.Count > 0 && items[^1] == 0 ? OpenRepeatAction.End : OpenRepeatAction.Next);
        var session = new ReaderSession(Build(Root()));

        var result = session.Read(new ReadOnlySequence<byte>(new byte[] { 9, 1, 2, 0, 5, 6 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Items, Is.EqualTo(new byte[] { 1, 2, 0 }));
        Assert.That(session.BytesRead, Is.EqualTo(4));
    }

    [Test]
    public void Read_SinkPeeksSentinel_StopsBeforeIt()
    {
        var sink = new Sink((context, items) =>
        {
            Assert.That(context.TryPeek(1, out var next), Is.True);
            return next.Length == 1 && next.FirstSpan[0] == 0xFF ? OpenRepeatAction.End : OpenRepeatAction.Next;
        });
        var session = new ReaderSession(Build(Root()));

        var result = session.Read(new ReadOnlySequence<byte>(new byte[] { 9, 1, 2, 0xFF, 3 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Items, Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(session.BytesRead, Is.EqualTo(3));
    }

    [Test]
    public void Read_SinkAnswersDefault_ReadsUntilEnd()
    {
        var sink = new Sink((context, items) => OpenRepeatAction.Default);

        var result = new ReaderSession(Build(Root())).Read(new ReadOnlySequence<byte>(new byte[] { 9, 1, 2, 3 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Items, Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void Read_Context_DescribesEachNextItem()
    {
        var seen = new List<(int Index, long Remaining, bool AtDefaultEnd)>();
        var sink = new Sink((context, items) =>
        {
            Assert.That(context.Path, Is.EqualTo("Root.Items"));
            seen.Add((context.Index, context.Remaining, context.AtDefaultEnd));
            return OpenRepeatAction.Default;
        });

        new ReaderSession(Build(Root())).Read(new ReadOnlySequence<byte>(new byte[] { 9, 1, 2 }), sink);

        Assert.That(seen, Is.EqualTo(new[] { (0, 2L, false), (1, 1L, false), (2, 0L, true) }));
    }

    [Test]
    public void Read_SinkAnswersNextAtEndOfInput_NeedsMoreData()
    {
        var sink = new Sink((context, items) => items.Count < 3 ? OpenRepeatAction.Next : OpenRepeatAction.End);

        var result = new ReaderSession(Build(Root())).Read(new ReadOnlySequence<byte>(new byte[] { 9, 1 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_SinkEndsBeforeFirstItem_ReadsEmptyList()
    {
        var sink = new Sink((context, items) => OpenRepeatAction.End);

        var result = new ReaderSession(Build(Root())).Read(new ReadOnlySequence<byte>(new byte[] { 9, 1, 2 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Items, Is.Empty);
    }

    [Test]
    public void Write_SourceDecidesItems_WritesThemWithoutCount()
    {
        var indexes = new List<int>();
        var source = new Source([1, 2, 3], indexes);
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(Build(Root()), output).Write(source);

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 9, 1, 2, 3 }));
        Assert.That(indexes, Is.EqualTo(new[] { 0, 1, 2, 3 }));
    }

    [Test]
    public void Write_SourceWithNoItems_WritesEmptyList()
    {
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(Build(Root()), output).Write(new Source([], []));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 9 }));
    }

    [Test]
    public void Read_OpenRepeatItems_GetUnknownCount()
    {
        var sink = new Sink((context, items) => OpenRepeatAction.Default);

        new ReaderSession(Build(Root())).Read(new ReadOnlySequence<byte>(new byte[] { 9, 1, 2 }), sink);

        Assert.That(sink.Counts, Is.EqualTo(new[] { -1, -1, -1, -1 }));
    }

    [Test]
    public void Write_NestedOpenRepeatWithUnknownCount_ProbesTheScopeThatHoldsTheRepeat()
    {
        var repeat = new SchemaRepeat { Name = "Items" };
        repeat.MemberList.Add(Field("Byte", "sys.uint8"));
        var root = Group("Root", [], Field("Head", "sys.uint8"), Group("Body", [], repeat));
        var output = new ArrayBufferWriter<byte>();

        var result = new WriterSession(Build(root), output).Write(new HierarchicalSource([1, 2]));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 9, 1, 2 }));
    }

    // one scope for everything: the root (which holds the repeat) and the items.
    private sealed class Sink(Func<OpenRepeatReadContext, List<byte>, OpenRepeatAction> decide) : IOpenRepeatSink
    {
        public List<byte> Items { get; } = [];
        // the counts EnterItem and ExitItem received, in call order.
        public List<int> Counts { get; } = [];

        public OpenRepeatAction NextItem(OpenRepeatReadContext context) => decide(context, Items);

        public void SetField(FieldContext context, LogicalField value)
        {
            if (context.Path == "Root.Items.Byte")
            {
                Items.Add((byte)value.Value!);
            }
        }

        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterItem(RepeatContext context, int index, int count)
        {
            Counts.Add(count);
            return this;
        }

        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) => Counts.Add(count);
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }

    private sealed class Source(byte[] items, List<int> indexes) : IOpenRepeatSource
    {
        private int _item = -1;

        public OpenRepeatAction NextItem(OpenRepeatContext context)
        {
            indexes.Add(context.Index);
            return context.Index < items.Length ? OpenRepeatAction.Next : OpenRepeatAction.End;
        }

        public SourceResult GetField(FieldContext context) => context.Path switch
        {
            "Root.Head" => SourceResult.Provided(new LogicalField("Head", typeof(byte), (byte)9)),
            "Root.Items.Byte" => SourceResult.Provided(new LogicalField("Byte", typeof(byte), items[_item])),
            _ => SourceResult.NoValue(),
        };

        public IValueSource EnterGroup(GroupContext context) => this;
        public int GetCount(RepeatContext context) => throw new NotSupportedException("an open repeat source is not asked for a count");

        public IValueSource EnterItem(RepeatContext context, int index)
        {
            _item = index;
            return this;
        }

        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => throw new NotSupportedException();
    }

    // root scope -> Body scope (holds the open repeat, answers -1 and EndOfData) -> item scopes; each scope only knows its own fields.
    private sealed class HierarchicalSource(byte[] items, int level = 0, int item = -1) : IValueSource
    {
        public SourceResult GetField(FieldContext context) => (level, context.Path) switch
        {
            (0, "Root.Head") => SourceResult.Provided(new LogicalField("Head", typeof(byte), (byte)9)),
            (1, "Root.Body.Items.Byte") => context.Instance.Last < items.Length ? SourceResult.NoValue() : SourceResult.EndOfData(),
            (2, "Root.Body.Items.Byte") => SourceResult.Provided(new LogicalField("Byte", typeof(byte), items[item])),
            _ => throw new InvalidOperationException($"'{context.Path}' asked on scope level {level}."),
        };

        public IValueSource EnterGroup(GroupContext context) => new HierarchicalSource(items, level + 1);
        public int GetCount(RepeatContext context) => -1;
        public IValueSource EnterItem(RepeatContext context, int index) => new HierarchicalSource(items, level + 1, index);
        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();
        public IValueSource EnterChoice(ChoiceContext context) => throw new NotSupportedException();
    }
}
