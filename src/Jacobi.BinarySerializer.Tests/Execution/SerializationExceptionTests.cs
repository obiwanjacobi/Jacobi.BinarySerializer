using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Schema;
using static Jacobi.BinarySerializer.Tests.Processors.ProcessorTestHelpers;

namespace Jacobi.BinarySerializer.Tests.Execution;

/// <summary>
/// The facts of a failed read or write (<see cref="SerializationException"/>): category, origin, location and the
/// expected and actual value, for the failures of the engine and of processors.
/// </summary>
public class SerializationExceptionTests
{
    // 0x00 A, 0x01 Magic (constant 0x42), 0x02 B
    private static SchemaGroup WithConstant()
        => Group("Root", [],
            Field("A", "sys.uint8"),
            new SchemaField { Name = "Magic", DataType = "sys.uint8", Value = "0x42" },
            Field("B", "sys.uint8"));

    private static Serializer CreateSerializer()
        => new SerializerBuilder()
            .AddSchemas(new SchemaSetBuilder().Compile())
            .AddProcessors(new ProcessorManager())
            .Build();

    [Test]
    public void Read_ConstantDoesNotMatch_IsAnExpectedValueOfTheEngine()
    {
        var plan = Build(WithConstant());

        var failure = Assert.Throws<SerializationException>(() =>
            new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 1, 0x41, 2 }), new DictSink()))!;

        Assert.That(failure.Category, Is.EqualTo(SerializationCategories.ExpectedValue));
        Assert.That(failure.Origin, Is.EqualTo(SerializationException.EngineOrigin));
        Assert.That(failure.Direction, Is.EqualTo(SerializationDirection.Read));
        Assert.That(failure.Path?.ToString(), Is.EqualTo("Root.Magic"));
        Assert.That(failure.Position, Is.EqualTo(1));
        Assert.That(failure.Expected, Is.EqualTo(0x42));
        Assert.That(failure.Actual, Is.EqualTo(0x41));
        Assert.That(failure.Message, Does.Contain("read at byte 1; engine: expectedValue"));
    }

    [Test]
    public void Write_ConstantDoesNotMatch_IsAnExpectedValueOfTheEngine()
    {
        var plan = Build(WithConstant());
        var values = new Dictionary<string, object?> { ["Root.A"] = (byte)1, ["Root.Magic"] = (byte)0x41, ["Root.B"] = (byte)2 };

        var failure = Assert.Throws<SerializationException>(() =>
            new WriterSession(plan, new ArrayBufferWriter<byte>(), valueFieldsUseModel: true).Write(new DictSource(values)))!;

        Assert.That(failure.Category, Is.EqualTo(SerializationCategories.ExpectedValue));
        Assert.That(failure.Direction, Is.EqualTo(SerializationDirection.Write));
        Assert.That(failure.Path?.ToString(), Is.EqualTo("Root.Magic"));
        Assert.That(failure.Position, Is.EqualTo(1));
    }

    [Test]
    public void Deserialize_InputEndsEarly_IsAnEndOfData()
    {
        var plan = Build(WithConstant());

        var failure = Assert.Throws<SerializationException>(() =>
            CreateSerializer().Deserialize(plan, new ReadOnlySequence<byte>(new byte[] { 1, 0x42 }), new DictSink()))!;

        Assert.That(failure.Category, Is.EqualTo(SerializationCategories.EndOfData));
        Assert.That(failure.Origin, Is.EqualTo(SerializationException.EngineOrigin));
        Assert.That(failure.Path?.ToString(), Is.EqualTo("Root.B"));
        Assert.That(failure.Position, Is.EqualTo(2));
    }

    [Test]
    public void Read_CrcDoesNotMatch_IsTheProcessorsOwnCategory()
    {
        var root = Group("Root", [Ref("crc")], Field("Value", "sys.uint8"));
        var bytes = Write(root, new() { ["Root.Value"] = (byte)7 }, out var written);
        Assert.That(written, Is.EqualTo(WriteResult.Success));
        bytes[0] ^= 1;

        var failure = Assert.Throws<SerializationException>(() =>
            new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>(bytes), new DictSink()))!;

        Assert.That(failure.Origin, Is.EqualTo("sys.crc"));
        Assert.That(failure.Category, Is.EqualTo("checksum"));
        Assert.That(failure.Path?.ToString(), Is.EqualTo("Root"));
        Assert.That(failure.Expected, Is.Not.Null);
        Assert.That(failure.Actual, Is.Not.EqualTo(failure.Expected));
    }

    [Test]
    public void Read_ProcessorThrows_IsWrappedWithTheProcessorAsOrigin()
    {
        var root = Group("Root", [],
            Field("A", "sys.uint8"),
            Field("Name", "sys.string", [Ref("string", ("byteLength", "2"), ("validBytes", "0x20-0x7F"))]));

        var failure = Assert.Throws<SerializationException>(() =>
            new ReaderSession(Build(root)).Read(new ReadOnlySequence<byte>(new byte[] { 1, 0x41, 0x05 }), new DictSink()))!;

        Assert.That(failure.Origin, Is.EqualTo("sys.string"));
        Assert.That(failure.Category, Is.EqualTo(SerializationCategories.InvalidData));
        Assert.That(failure.Path?.ToString(), Is.EqualTo("Root.Name"));
        Assert.That(failure.Position, Is.EqualTo(1));
        Assert.That(failure.InnerException, Is.Not.Null);
        Assert.That(failure.Reason, Is.EqualTo(failure.InnerException!.Message));
    }

    [Test]
    public void Read_ValueModelThrows_PassesAsItIs()
    {
        var plan = Build(WithConstant());

        Assert.Throws<ArgumentException>(() =>
            new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 1, 0x42, 2 }), new ThrowingSink()));
    }

    /// <summary>A value model that rejects every value it gets.</summary>
    private sealed class ThrowingSink : IValueSink
    {
        public void SetField(FieldContext context, LogicalField value) => throw new ArgumentException("rejected by the model");
        public IValueSink EnterGroup(GroupContext context) => this;
        public IValueSink EnterItem(RepeatContext context, int index, int count) => this;
        public IValueSink EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }
}
