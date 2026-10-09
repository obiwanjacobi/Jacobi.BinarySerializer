using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using Jacobi.BinarySerializer.Tests.Execution;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Processors;

public class PublishNamespaceTests
{
    [Test]
    public void Crc_PubNs_PublishesTheCalculatedCrcOnWriteAndRead()
    {
        var crc = WithPubNs(Ref("crc", ("algorithm", "crc32")), "hdr");
        var root = Group("Root", [crc], Field("A", "UInt8"), Field("B", "UInt8"));
        var plan = Build(root);

        var output = new ArrayBufferWriter<byte>();
        var writer = new WriterSession(plan, output);
        Assert.That(writer.Write(new DictSource(new() { ["Root.A"] = (byte)1, ["Root.B"] = (byte)2 })), Is.EqualTo(WriteResult.Success));

        var reader = new ReaderSession(plan);
        Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), new DictSink()), Is.EqualTo(ReadResult.Success));

        var written = writer.ResolvePublished(new PublishedValueKey("hdr", "value"), "Root");
        var read = reader.ResolvePublished(new PublishedValueKey("hdr", "value"), "Root");
        Assert.That(written, Is.TypeOf<ulong>());
        Assert.That(read, Is.EqualTo(written));
        Assert.That(() => writer.ResolvePublished(new PublishedValueKey("sys.crc", "value"), "Root"), Throws.Exception);
    }

    [Test]
    public void String_PubNs_PublishesTheCharCountOnWriteAndRead()
    {
        var str = WithPubNs(Ref("string", ("byteLength", "8")), "name");
        var root = Group("Root", [], Field("A", "String", [str]));
        var plan = Build(root);

        var output = new ArrayBufferWriter<byte>();
        var writer = new WriterSession(plan, output);
        Assert.That(writer.Write(new DictSource(new() { ["Root.A"] = "h\u00E9llo" })), Is.EqualTo(WriteResult.Success));

        var reader = new ReaderSession(plan);
        Assert.That(reader.Read(new ReadOnlySequence<byte>(output.WrittenMemory), new DictSink()), Is.EqualTo(ReadResult.Success));

        var key = new PublishedValueKey("name", "length");
        Assert.That(writer.ResolvePublished(key, "Root"), Is.EqualTo(5));
        Assert.That(reader.ResolvePublished(key, "Root"), Is.EqualTo(5));
        Assert.That(() => writer.ResolvePublished(new PublishedValueKey("sys.string", "length"), "Root"), Throws.Exception);
    }

    private static SchemaProcessorRef WithPubNs(SchemaProcessorRef source, string ns)
    {
        var result = new SchemaProcessorRef { Processor = source.Processor, PublishNamespace = ns };
        result.PropertyList.AddRange(source.PropertyList);
        return result;
    }
}


