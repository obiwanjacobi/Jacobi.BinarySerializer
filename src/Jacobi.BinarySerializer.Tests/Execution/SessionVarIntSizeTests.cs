using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>
/// A derived size field with a variable width (varint) takes the width its value needs.
/// </summary>
public class SessionVarIntSizeTests
{
    private static SchemaGroup Message(SchemaProcessorRef[]? bodyProcessors = null)
    {
        var body = new SchemaGroup { Name = "Body", ByteSize = new SchemaNodeRef { Path = "Root.Len" } };
        if (bodyProcessors is not null)
        {
            body.ProcessorsList.AddRange(bodyProcessors);
        }
        body.MemberList.Add(new SchemaField { Name = "Blob", DataType = "Bytes" });
        return Group("Root", [], Field("Len", "UInt32", [Ref("varint")]), body);
    }

    private static byte[] WriteBlob(SchemaGroup root, int length)
    {
        var output = new ArrayBufferWriter<byte>();
        var source = new DictSource(new() { ["Root.Body.Blob"] = new byte[length] });
        Assert.That(new WriterSession(Build(root), output).Write(source), Is.EqualTo(WriteResult.Success));
        return output.WrittenSpan.ToArray();
    }

    [TestCase(5, new byte[] { 5 })]
    [TestCase(127, new byte[] { 127 })]
    [TestCase(128, new byte[] { 0x80, 0x01 })]
    [TestCase(300, new byte[] { 0xAC, 0x02 })]
    public void Write_SizeWidthFollowsTheValue(int length, byte[] expectedPrefix)
    {
        var bytes = WriteBlob(Message(), length);

        Assert.That(bytes.Length, Is.EqualTo(expectedPrefix.Length + length));
        Assert.That(bytes[..expectedPrefix.Length], Is.EqualTo(expectedPrefix));
    }

    [Test]
    public void RoundTrip_WideSize_ReadsTheBlobBack()
    {
        var plan = Build(Message());
        var output = new ArrayBufferWriter<byte>();
        new WriterSession(plan, output).Write(new DictSource(new() { ["Root.Body.Blob"] = new byte[200] }));

        var sink = new DictSink();
        Assert.That(new ReaderSession(plan).Read(new ReadOnlySequence<byte>(output.WrittenMemory), sink), Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.Len"], Is.EqualTo(200u));
        Assert.That(((byte[])sink.Values["Root.Body.Blob"]!).Length, Is.EqualTo(200));
    }

    [Test]
    public void Write_WidthChangeWithLayoutInsideTheGroup_Throws()
    {
        var root = Message([Ref("align", ("bytes", "4"))]);
        var output = new ArrayBufferWriter<byte>();
        var source = new DictSource(new() { ["Root.Body.Blob"] = new byte[200] });

        Assert.That(() => new WriterSession(Build(root), output).Write(source), Throws.Exception);
    }
}
