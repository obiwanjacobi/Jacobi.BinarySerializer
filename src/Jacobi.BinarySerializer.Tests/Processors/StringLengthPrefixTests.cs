using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

/// <summary>
/// A string preceded by its length field: the field-level byteLength refers to the length field.
/// </summary>
public class StringLengthPrefixTests
{
    private static SchemaGroup LengthPrefixedString()
    {
        var text = new SchemaField
        {
            Name = "Text",
            DataType = "sys.string",
            ByteLength = new SchemaNodeRef { Path = "Root.Len" },
            ProcessorsList = [Ref("string")]
        };
        return Group("Root", [], Field("Len", "sys.uint8"), text, Field("Tail", "sys.uint8"));
    }

    [Test]
    public void Read_LengthFieldRef_TakesThatManyBytesForTheString()
    {
        var plan = Build(LengthPrefixedString());
        var sink = new DictSink();

        var result = new ReaderSession(plan).Read(
            new ReadOnlySequence<byte>(new byte[] { 3, (byte)'a', (byte)'b', (byte)'c', 9 }), sink);

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(sink.Values["Root.Len"], Is.EqualTo((byte)3));
        Assert.That(sink.Values["Root.Text"], Is.EqualTo("abc"));
        Assert.That(sink.Values["Root.Tail"], Is.EqualTo((byte)9));
    }

    [Test]
    public void Write_LengthFieldRef_PadsTheStringToThatLength()
    {
        var plan = Build(LengthPrefixedString());
        var output = new ArrayBufferWriter<byte>();
        var values = new Dictionary<string, object?> { ["Root.Len"] = (byte)5, ["Root.Text"] = "abc", ["Root.Tail"] = (byte)9 };

        var result = new WriterSession(plan, output).Write(new DictSource(values));

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, (byte)'a', (byte)'b', (byte)'c', 0, 0, 9 }));
    }
}
