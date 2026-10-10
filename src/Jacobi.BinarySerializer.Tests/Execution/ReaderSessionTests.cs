using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

// Mirror of WriterSessionTests: keep the test names and order in sync.
public class ReaderSessionTests
{
    [Test]
    public void Read_FixedWidthFields_InOrder()
    {
        var root = Group("Root", [], Field("A"), Group("G", [], Field("B", "sys.uint8")));

        var (result, sink) = Run(root, [1, 0, 0, 0, 7]);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?> { ["Root.A"] = 1, ["Root.G.B"] = (byte)7 }));
    }

    [Test]
    public void Read_NestedGroup_EntersAndCompletesGroupScope()
    {
        var root = Group("Root", [], Field("A"), Group("G", [], Field("B")));

        var (_, sink) = Run(root, [1, 0, 0, 0, 2, 0, 0, 0]);

        Assert.That(sink.Events, Is.EqualTo(new[] { "Enter:Root.G", "Complete:Root.G", "Complete:Root" }));
    }

    [Test]
    public void Read_NotEnoughData_ReturnsNeedMoreData()
    {
        var root = Group("Root", [], Field("A"), Field("B"));

        var (result, _) = Run(root, [1, 0, 0, 0, 2, 0]);

        Assert.That(result, Is.EqualTo(ReadResult.NeedMoreData));
    }

    [Test]
    public void Read_InvalidBytes_Throws()
    {
        var root = Group("Root", [], Field("A", "sys.datetime"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => Run(root, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F]));

        Assert.That(ex!.Message, Does.Contain("Root.A"));
    }

    [Test]
    public void Read_ValueProcessor_TransformsDecodedValue()
    {
        var root = Group("Root", [], FieldWith("A", Ref("scale")));

        var (_, sink) = Run(root, [2, 0, 0, 0]);

        Assert.That(sink.Values["Root.A"], Is.EqualTo(1));
    }

    [Test]
    public void Read_FieldProcessor_OverridesDefaultDecoding()
    {
        var root = Group("Root", [], FieldWith("A", Ref("bigendian")));

        var (_, sink) = Run(root, [0, 0, 0, 1]);

        Assert.That(sink.Values["Root.A"], Is.EqualTo(1));
    }

    [Test]
    public void Read_LayoutFailure_ReturnsFailure()
    {
        var root = Group("Root", [Ref("failing")], Field("A"));

        var (result, _) = Run(root, [1, 0, 0, 0]);

        Assert.That(result, Is.EqualTo(ReadResult.Failure));
    }

    [Test]
    public void Read_RootStreamProcessor_UnframesPayload()
    {
        var root = Group("Root", [Ref("stream")], Field("A"));

        var (result, sink) = Run(root, [0xFF, 1, 0, 0, 0]);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.A"], Is.EqualTo(1));
    }

    [Test]
    public void Read_LayoutInheritedByNestedGroup_BeginsAndEndsOnce()
    {
        var log = new List<string>();
        var root = Group("Root", [Ref("layout")], Field("A"), Group("G", [], Field("B")));

        var (_, sink) = Run(root, [1, 0, 0, 0, 2, 0, 0, 0], log);

        Assert.That(log, Is.EqualTo(new[] { "Begin:Root", "Read:Root.A", "Read:Root.G.B", "End:Root" }));
        Assert.That(sink.Values, Is.EqualTo(new Dictionary<string, object?> { ["Root.A"] = 1, ["Root.G.B"] = 2 }));
    }

    [Test]
    public void Read_NestedGroupWithOwnLayout_BeginsAndEndsSeparately()
    {
        var log = new List<string>();
        var root = Group("Root", [Ref("layout")], Field("A"), Group("G", [Ref("layout")], Field("B")));

        Run(root, [1, 0, 0, 0, 2, 0, 0, 0], log);

        Assert.That(log, Is.EqualTo(new[]
        {
            "Begin:Root", "Read:Root.A", "Begin:Root.G", "Read:Root.G.B", "End:Root.G", "End:Root"
        }));
    }

    private static (ReadResult Result, DictSink Sink) Run(SchemaGroup root, byte[] data, List<string>? log = null)
    {
        var plan = Build(root, log);
        var sink = new DictSink();
        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(data), sink);
        return (result, sink);
    }
}
