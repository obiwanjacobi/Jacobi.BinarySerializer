using System.Buffers;
using Jacobi.BinarySerializer.Descriptors;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processor;

public class DefaultProcessorTests
{
    [Test]
    public void Stream_Write_CopiesUnreadInputAndConsumesIt()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>([1, 2, 3, 4]));
        reader.Advance(1);
        var output = new ArrayBufferWriter<byte>();

        var result = new DefaultStreamProcessor().Write(ref reader, output, new StreamProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! });

        Assert.That(result, Is.EqualTo(WriteResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 2, 3, 4 }));
        Assert.That(reader.End, Is.True);
    }

    [Test]
    public void Stream_Read_CopiesUnreadInputAndConsumesIt()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>([5, 6]));
        var output = new ArrayBufferWriter<byte>();

        var result = new DefaultStreamProcessor().Read(ref reader, output, new StreamProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! });

        Assert.That(result, Is.EqualTo(ReadResult.Success));
        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 5, 6 }));
        Assert.That(reader.End, Is.True);
    }

    [Test]
    public void Layout_Write_ForwardsByteValues()
    {
        var output = new ArrayBufferWriter<byte>();
        var processor = new DefaultLayoutProcessor();
        var context = new LayoutProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! };

        processor.Write(output, new EncodedField("a", typeof(byte[]), new byte[] { 1, 2 }, 16), context);
        processor.Write(output, new EncodedField("b", typeof(byte[]), new ReadOnlyMemory<byte>([3]), 8), context);

        Assert.That(output.WrittenSpan.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public void Layout_Write_NonByteValue_Fails()
    {
        var output = new ArrayBufferWriter<byte>();
        var context = new LayoutProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! };

        var result = new DefaultLayoutProcessor().Write(output, new EncodedField("a", typeof(int), 42, 32), context);

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
        Assert.That(output.WrittenCount, Is.Zero);
    }

    [Test]
    public void Layout_Read_PassesUnreadBytesOn()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>([7, 8]));
        var context = new LayoutProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! };

        var result = new DefaultLayoutProcessor().Read(ref reader, context);
        var encoded = result.Value;

        Assert.That(result.Status, Is.EqualTo(ReadResult.Success));
        Assert.That(encoded.Value, Is.EqualTo(new byte[] { 7, 8 }));
        Assert.That(encoded.BitWidth, Is.EqualTo(16));
        Assert.That(reader.End, Is.True);
    }

    [Test]
    public void Layout_Read_FixedWidthField_ConsumesOnlyTheFieldBytes()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>([1, 0, 0, 0, 9, 9]));
        var context = new LayoutProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! };
        context.Field = CreateField("Int32");

        var result = new DefaultLayoutProcessor().Read(ref reader, context);
        var encoded = result.Value;

        Assert.That(result.Status, Is.EqualTo(ReadResult.Success));
        Assert.That(encoded.Value, Is.EqualTo(new byte[] { 1, 0, 0, 0 }));
        Assert.That(encoded.BitWidth, Is.EqualTo(32));
        Assert.That(reader.Remaining, Is.EqualTo(2));
    }

    [Test]
    public void Layout_Read_NotEnoughData_NeedsMoreDataAndDoesNotConsume()
    {
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>([1, 0]));
        var context = new LayoutProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! };
        context.Field = CreateField("Int32");

        var result = new DefaultLayoutProcessor().Read(ref reader, context);

        Assert.That(result.Status, Is.EqualTo(ReadResult.NeedMoreData));
        Assert.That(reader.Remaining, Is.EqualTo(2));
    }

    [Test]
    public void Field_Write_EncodesValueAsFixedWidthBytes()
    {
        var context = new FieldProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! };
        context.Field = CreateField("UInt16");

        var encoded = new DefaultFieldProcessor().Write(new LogicalField("F", typeof(int), 258), context).Value;

        Assert.That(encoded.Value, Is.EqualTo(new byte[] { 2, 1 }));
        Assert.That(encoded.BitWidth, Is.EqualTo(16));
    }

    [Test]
    public void Field_Write_ValueDoesNotFit_ThrowsWithPath()
    {
        var context = new FieldProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! };
        context.Field = CreateField("UInt8");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new DefaultFieldProcessor().Write(new LogicalField("F", typeof(int), 999), context));

        Assert.That(ex!.Message, Does.Contain("Root.F").And.Contain("sys.uint8"));
    }

    [Test]
    public void Field_Read_DecodesBytesToTypedValue()
    {
        var context = new FieldProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! };
        context.Field = CreateField("Int16");

        var logical = new DefaultFieldProcessor().Read(new EncodedField("F", typeof(byte[]), new byte[] { 0xFE, 0xFF }, 16), context).Value;

        Assert.That(logical.Value, Is.EqualTo((short)-2));
        Assert.That(logical.LogicalType, Is.EqualTo(typeof(short)));
    }

    [Test]
    public void DefaultStages_WriteThenRead_RoundTripsAValue()
    {
        var field = CreateField("Double");
        var writeContext = new FieldProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! };
        writeContext.Field = field;
        var layoutWrite = new LayoutProcessorContext(NewWriter()) { ProcessorProperties = [], Services = null! };
        layoutWrite.Field = field;
        var output = new ArrayBufferWriter<byte>();

        var encoded = new DefaultFieldProcessor().Write(new LogicalField("F", typeof(double), 2.5), writeContext).Value;
        new DefaultLayoutProcessor().Write(output, encoded, layoutWrite);

        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(output.WrittenMemory));
        var layoutRead = new LayoutProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! };
        layoutRead.Field = field;
        var readContext = new FieldProcessorContext(NewReader()) { ProcessorProperties = [], Services = null! };
        readContext.Field = field;

        var readEncoded = new DefaultLayoutProcessor().Read(ref reader, layoutRead).Value;
        var logical = new DefaultFieldProcessor().Read(readEncoded, readContext).Value;

        Assert.That(logical.Value, Is.EqualTo(2.5));
    }

    private static ReaderSession NewReader()
    {
        var plan = new ExecutionPlanBuilder(new ProcessorManager()).Build(new SchemaGroup { Name = "Root" });
        return new ReaderSession(plan);
    }

    private static WriterSession NewWriter()
    {
        var plan = new ExecutionPlanBuilder(new ProcessorManager()).Build(new SchemaGroup { Name = "Root" });
        return new WriterSession(plan, new ArrayBufferWriter<byte>());
    }

    private static FieldInfo CreateField(SchemaDataType type)
        => new()
        {
            Name = "F",
            Path = "Root.F",
            Field = new SchemaField { Name = "F", DataType = type },
            DataType = DataTypeRegistry.CreateDefault().Get(type),
            Pipeline = new ProcessorPipeline([]),
        };
}
