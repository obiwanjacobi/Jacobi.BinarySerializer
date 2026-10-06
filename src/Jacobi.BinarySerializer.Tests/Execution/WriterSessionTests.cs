using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Execution.SessionTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

// Mirror of ReaderSessionTests: keep the test names and order in sync.
public class WriterSessionTests
{
    [Test]
    public void Write_FixedWidthFields_InOrder()
    {
        var root = Group("Root", [], Field("A"), Group("G", [], Field("B", SchemaDataType.UInt8)));

        var (result, bytes, _) = Run(root, new() { ["Root.A"] = 1, ["Root.G.B"] = (byte)7 });

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0, 0, 7 }));
    }

    [Test]
    public void Write_NestedGroup_EntersGroupScope()
    {
        var root = Group("Root", [], Field("A"), Group("G", [], Field("B")));

        var (_, _, events) = Run(root, new() { ["Root.A"] = 1, ["Root.G.B"] = 2 });

        Assert.That(events, Is.EqualTo(new[] { "Enter:Root.G" }));
    }

    [Test]
    public void Write_MissingValue_Throws()
    {
        var root = Group("Root", [], Field("A"));

        var ex = Assert.Throws<InvalidOperationException>(() => Run(root, []));

        Assert.That(ex!.Message, Does.Contain("Root.A"));
    }

    [Test]
    public void Write_ValueDoesNotFit_Throws()
    {
        var root = Group("Root", [], Field("A", SchemaDataType.UInt8));

        var ex = Assert.Throws<InvalidOperationException>(() => Run(root, new() { ["Root.A"] = 300 }));

        Assert.That(ex!.Message, Does.Contain("Root.A"));
    }

    [Test]
    public void Write_ValueProcessor_TransformsValueBeforeEncoding()
    {
        var root = Group("Root", [], FieldWith("A", Ref("scale")));

        var (_, bytes, _) = Run(root, new() { ["Root.A"] = 1 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 2, 0, 0, 0 }));
    }

    [Test]
    public void Write_FieldProcessor_OverridesDefaultEncoding()
    {
        var root = Group("Root", [], FieldWith("A", Ref("bigendian")));

        var (_, bytes, _) = Run(root, new() { ["Root.A"] = 1 });

        Assert.That(bytes, Is.EqualTo(new byte[] { 0, 0, 0, 1 }));
    }

    [Test]
    public void Write_LayoutFailure_ReturnsFailure()
    {
        var root = Group("Root", [Ref("failing")], Field("A"));

        var (result, _, _) = Run(root, new() { ["Root.A"] = 1 });

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    [Test]
    public void Write_RootStreamProcessor_FramesPayload()
    {
        var root = Group("Root", [Ref("stream")], Field("A"));

        var (result, bytes, _) = Run(root, new() { ["Root.A"] = 1 });

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xFF, 1, 0, 0, 0 }));
    }

    [Test]
    public void Write_LayoutInheritedByNestedGroup_BeginsAndEndsOnce()
    {
        var log = new List<string>();
        var root = Group("Root", [Ref("layout")], Field("A"), Group("G", [], Field("B")));

        var (_, bytes, _) = Run(root, new() { ["Root.A"] = 1, ["Root.G.B"] = 2 }, log);

        Assert.That(log, Is.EqualTo(new[] { "Begin:Root", "Write:Root.A", "Write:Root.G.B", "End:Root" }));
        Assert.That(bytes, Is.EqualTo(new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 }));
    }

    [Test]
    public void Write_NestedGroupWithOwnLayout_BeginsAndEndsSeparately()
    {
        var log = new List<string>();
        var root = Group("Root", [Ref("layout")], Field("A"), Group("G", [Ref("layout")], Field("B")));

        Run(root, new() { ["Root.A"] = 1, ["Root.G.B"] = 2 }, log);

        Assert.That(log, Is.EqualTo(new[]
        {
            "Begin:Root", "Write:Root.A", "Begin:Root.G", "Write:Root.G.B", "End:Root.G", "End:Root"
        }));
    }

    private static (WriteResult Result, byte[] Bytes, List<string> Events) Run(
        SchemaGroup root, Dictionary<string, object?> values, List<string>? log = null)
    {
        var plan = Build(root, log);
        var output = new ArrayBufferWriter<byte>();
        var events = new List<string>();
        var result = new WriterSession(plan, output).Write(new DictSource(values, events));
        return (result, output.WrittenSpan.ToArray(), events);
    }
}
