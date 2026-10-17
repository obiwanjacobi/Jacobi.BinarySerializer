using System.Buffers;
using Jacobi.BinarySerializer.Execution;
using Jacobi.BinarySerializer.Processor;
using Jacobi.BinarySerializer.Processors;
using Jacobi.BinarySerializer.Schema;

namespace Jacobi.BinarySerializer.Tests.Processors;

/// <summary>Round trips of sys.bitpacker as the packing processor of a packedGroup, through the Serializer API.</summary>
public class BitPackerSerializerTests
{
    private static readonly SchemaName MsgName = new("Doc.Msg");

    private static string Json(string flagsMembers, int byteLength, string? processorProperties = null, string? groupProcessors = null)
        => $$"""
            { "name": "Doc", "members": [
              { "name": "Msg", "kind": "group", {{groupProcessors}}
                "members": [
                  { "name": "Header", "kind": "field", "datatype": "sys.uint8" },
                  { "name": "Flags", "kind": "packed", "byteLength": {{byteLength}},
                    "processors": [ { "name": "sys.bitpacker"{{processorProperties}} } ],
                    "members": [ {{flagsMembers}} ] },
                  { "name": "Trailer", "kind": "field", "datatype": "sys.uint8" }
                ] }
            ] }
            """;

    private const string ThreeFields = """
        { "name": "A", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 3 },
        { "name": "B", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 5 },
        { "name": "C", "kind": "field", "datatype": "sys.uint16", "sys.bitpacker.bits": 13 }
        """;

    private static Serializer CreateSerializer(string schema, bool xml = false)
    {
        var builder = new SchemaSetBuilder();
        if (xml)
        {
            builder.LoadFromXml(schema);
        }
        else
        {
            builder.LoadFromJson(schema);
        }

        var manager = new ProcessorManager();
        manager.Register(new ProcessorFactory());
        return new SerializerBuilder().AddSchemas(builder.Compile()).AddProcessors(manager).Build();
    }

    private static Model Values(params (string Path, object Value)[] values)
    {
        var model = new Model();
        foreach (var (path, value) in values)
        {
            model.Set(path, value);
        }
        return model;
    }

    private static Model ThreeFieldValues()
        => Values(("Msg.Header", (byte)0xAA), ("Msg.Flags.A", (byte)5), ("Msg.Flags.B", (byte)17),
            ("Msg.Flags.C", (ushort)0x1234), ("Msg.Trailer", (byte)0xBB));

    private static (byte[] Bytes, Model Read) RoundTrip(Serializer serializer, Model input)
    {
        var output = new ArrayBufferWriter<byte>();
        serializer.Serialize(MsgName, input, output);
        var read = new Model();
        var consumed = serializer.Deserialize(MsgName, new ReadOnlySequence<byte>(output.WrittenMemory), read);
        Assert.That(consumed, Is.EqualTo(output.WrittenCount));
        return (output.WrittenSpan.ToArray(), read);
    }

    [Test]
    public void RoundTrip_LsbFirst_IsTheDefault()
    {
        var (bytes, read) = RoundTrip(CreateSerializer(Json(ThreeFields, 3)), ThreeFieldValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAA, 0x8D, 0x34, 0x12, 0xBB }));
        Assert.That(read.Values, Is.EqualTo(ThreeFieldValues().Values));
    }

    [Test]
    public void RoundTrip_MsbFirst()
    {
        var (bytes, read) = RoundTrip(CreateSerializer(Json(ThreeFields, 3, """, "bitorder": "big" """)), ThreeFieldValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAA, 0xB1, 0x91, 0xA0, 0xBB }));
        Assert.That(read.Values, Is.EqualTo(ThreeFieldValues().Values));
    }

    [Test]
    public void RoundTrip_BitsPropertyWithShortName()
    {
        const string fields = """
            { "name": "A", "kind": "field", "datatype": "sys.uint8", "bits": 3 },
            { "name": "B", "kind": "field", "datatype": "sys.uint8", "bits": 5 }
            """;
        var input = Values(("Msg.Header", (byte)1), ("Msg.Flags.A", (byte)5), ("Msg.Flags.B", (byte)17), ("Msg.Trailer", (byte)2));

        var (bytes, read) = RoundTrip(CreateSerializer(Json(fields, 1)), input);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x01, 0x8D, 0x02 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [TestCase("little")]
    [TestCase("big")]
    public void RoundTrip_SignedValuesAndBooleans(string bitOrder)
    {
        const string fields = """
            { "name": "A", "kind": "field", "datatype": "sys.int8", "sys.bitpacker.bits": 4 },
            { "name": "B", "kind": "field", "datatype": "sys.int16", "sys.bitpacker.bits": 12 },
            { "name": "C", "kind": "field", "datatype": "sys.bool", "sys.bitpacker.bits": 1 }
            """;
        var input = Values(("Msg.Header", (byte)1), ("Msg.Flags.A", (sbyte)-3), ("Msg.Flags.B", (short)-1000),
            ("Msg.Flags.C", true), ("Msg.Trailer", (byte)2));

        var (bytes, read) = RoundTrip(CreateSerializer(Json(fields, 3, $""", "bitorder": "{bitOrder}" """)), input);

        Assert.That(bytes, Has.Length.EqualTo(5));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void RoundTrip_DefaultsAreOneByteAndOneBitPerField()
    {
        const string fields = """
            { "name": "A", "kind": "field", "datatype": "sys.bool" },
            { "name": "B", "kind": "field", "datatype": "sys.uint8" },
            { "name": "C", "kind": "field", "datatype": "sys.bool" },
            { "name": "D", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 4 }
            """;
        const string schema = """
            { "name": "Doc", "members": [
              { "name": "Msg", "kind": "group", "members": [
                { "name": "Flags", "kind": "packed",
                  "processors": [ { "name": "sys.bitpacker" } ],
                  "members": [ FIELDS ] }
              ] }
            ] }
            """;
        var input = Values(("Msg.Flags.A", true), ("Msg.Flags.B", (byte)1), ("Msg.Flags.C", true), ("Msg.Flags.D", (byte)0xA));

        var (bytes, read) = RoundTrip(CreateSerializer(schema.Replace("FIELDS", fields)), input);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x57 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void Write_DefaultWidthOfOneBit_RejectsWiderValues()
    {
        const string fields = """{ "name": "A", "kind": "field", "datatype": "sys.uint8" }""";
        var plan = CreateSerializer(Json(fields, 1)).GetPlan(MsgName);
        var input = Values(("Msg.Header", (byte)0), ("Msg.Flags.A", (byte)2), ("Msg.Trailer", (byte)0));

        var result = new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(input);

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    [Test]
    public void RoundTrip_UnusedCarrierBitsAreZero()
    {
        const string fields = """{ "name": "A", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 5 }""";
        var input = Values(("Msg.Header", (byte)1), ("Msg.Flags.A", (byte)0x1F), ("Msg.Trailer", (byte)2));

        var (bytes, read) = RoundTrip(CreateSerializer(Json(fields, 2)), input);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x01, 0x1F, 0x00, 0x02 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void RoundTrip_ChildValueProcessorRunsBeforePacking()
    {
        const string fields = """
            { "name": "Level", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 4,
              "processors": [ { "name": "sys.scale", "scale": 10 } ] },
            { "name": "Mode", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 4 }
            """;
        var input = Values(("Msg.Header", (byte)1), ("Msg.Flags.Level", 1.5m), ("Msg.Flags.Mode", (byte)3), ("Msg.Trailer", (byte)2));

        var (bytes, read) = RoundTrip(CreateSerializer(Json(fields, 1)), input);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x01, 0x3F, 0x02 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void RoundTrip_CarrierIsLaidOutByTheGroupProcessor()
    {
        const string fields = """
            { "name": "A", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 4 },
            { "name": "B", "kind": "field", "datatype": "sys.uint16", "sys.bitpacker.bits": 12 }
            """;
        var input = Values(("Msg.Header", (byte)1), ("Msg.Flags.A", (byte)0xA), ("Msg.Flags.B", (ushort)0x123), ("Msg.Trailer", (byte)2));

        var serializer = CreateSerializer(Json(fields, 2, groupProcessors: """" "processors": [ { "name": "sys.bytepacker", "byteorder": "big" } ], """"));
        var (bytes, read) = RoundTrip(serializer, input);

        // the packed carrier is 3A 12 (lsb first); the group's byte order reverses it.
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x01, 0x12, 0x3A, 0x02 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void RoundTrip_CarrierLengthFromAReferencedField()
    {
        const string schema = """
            { "name": "Doc", "members": [
              { "name": "Msg", "kind": "group", "members": [
                { "name": "Length", "kind": "field", "datatype": "sys.uint8" },
                { "name": "Flags", "kind": "packed", "byteLength": { "ref": "Msg.Length" },
                  "processors": [ { "name": "sys.bitpacker" } ],
                  "members": [ { "name": "A", "kind": "field", "datatype": "sys.uint16", "sys.bitpacker.bits": 16 } ] }
              ] }
            ] }
            """;
        var input = Values(("Msg.Length", (byte)2), ("Msg.Flags.A", (ushort)0x1234));

        var (bytes, read) = RoundTrip(CreateSerializer(schema), input);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x02, 0x34, 0x12 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void RoundTrip_RepeatedPackedGroup()
    {
        const string schema = """
            { "name": "Doc", "members": [
              { "name": "Msg", "kind": "group", "members": [
                { "name": "Items", "kind": "repeat", "count": 3, "members": [
                  { "name": "Flags", "kind": "packed", "byteLength": 1,
                    "processors": [ { "name": "sys.bitpacker", "bitorder": "big" } ],
                    "members": [
                      { "name": "Kind", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 4 },
                      { "name": "Channel", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 4 }
                    ] },
                  { "name": "Data", "kind": "field", "datatype": "sys.uint8" }
                ] }
              ] }
            ] }
            """;
        var input = new Model();
        for (var i = 0; i < 3; i++)
        {
            input.Set($"Msg.Items.Flags.Kind[{i}]", (byte)(8 + i));
            input.Set($"Msg.Items.Flags.Channel[{i}]", (byte)(i + 1));
            input.Set($"Msg.Items.Data[{i}]", (byte)(0x10 + i));
        }

        var (bytes, read) = RoundTrip(CreateSerializer(schema), input);

        Assert.That(bytes, Is.EqualTo(new byte[] { 0x81, 0x10, 0x92, 0x11, 0xA3, 0x12 }));
        Assert.That(read.Values, Is.EqualTo(input.Values));
    }

    [Test]
    public void RoundTrip_Xml()
    {
        const string xml = """
            <schema name="Doc">
              <members>
                <group name="Msg">
                  <members>
                    <field name="Header" datatype="sys.uint8" />
                    <packed name="Flags" byteLength="3">
                      <processors><processor name="sys.bitpacker" bitorder="big" /></processors>
                      <members>
                        <field name="A" datatype="sys.uint8" sys.bitpacker.bits="3" />
                        <field name="B" datatype="sys.uint8" sys.bitpacker.bits="5" />
                        <field name="C" datatype="sys.uint16" sys.bitpacker.bits="13" />
                      </members>
                    </packed>
                    <field name="Trailer" datatype="sys.uint8" />
                  </members>
                </group>
              </members>
            </schema>
            """;

        var (bytes, read) = RoundTrip(CreateSerializer(xml, xml: true), ThreeFieldValues());

        Assert.That(bytes, Is.EqualTo(new byte[] { 0xAA, 0xB1, 0x91, 0xA0, 0xBB }));
        Assert.That(read.Values, Is.EqualTo(ThreeFieldValues().Values));
    }

    [Test]
    public void Deserialize_TruncatedCarrier_Throws()
    {
        var serializer = CreateSerializer(Json(ThreeFields, 3));

        Assert.That(() => serializer.Deserialize(MsgName, new ReadOnlySequence<byte>(new byte[] { 0xAA, 0x8D }), new Model()),
            Throws.InvalidOperationException);
    }

    [Test]
    public void Read_FieldsDoNotFitTheCarrier_Fails()
    {
        var plan = CreateSerializer(Json(ThreeFields, 2)).GetPlan(MsgName);

        var result = new ReaderSession(plan).Read(new ReadOnlySequence<byte>(new byte[] { 0xAA, 0x8D, 0x34, 0xBB }), new Model());

        Assert.That(result, Is.EqualTo(ReadResult.Failure));
    }

    [TestCase(9, 0, 0)]
    [TestCase(0, 32, 0)]
    [TestCase(0, 0, 8192)]
    public void Write_ValueWiderThanTheBits_Fails(int a, int b, int c)
    {
        var plan = CreateSerializer(Json(ThreeFields, 3)).GetPlan(MsgName);
        var input = Values(("Msg.Header", (byte)0), ("Msg.Flags.A", (byte)a), ("Msg.Flags.B", (byte)b),
            ("Msg.Flags.C", (ushort)c), ("Msg.Trailer", (byte)0));

        var result = new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(input);

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    [Test]
    public void Write_FieldsDoNotFitTheCarrier_Fails()
    {
        var plan = CreateSerializer(Json(ThreeFields, 2)).GetPlan(MsgName);

        var result = new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(ThreeFieldValues());

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    [Test]
    public void Write_BitsOutOfRangeForTheDataType_Fails()
    {
        const string fields = """{ "name": "A", "kind": "field", "datatype": "sys.uint8", "sys.bitpacker.bits": 9 }""";
        var plan = CreateSerializer(Json(fields, 2)).GetPlan(MsgName);
        var input = Values(("Msg.Header", (byte)0), ("Msg.Flags.A", (byte)1), ("Msg.Trailer", (byte)0));

        var result = new WriterSession(plan, new ArrayBufferWriter<byte>()).Write(input);

        Assert.That(result, Is.EqualTo(WriteResult.Failure));
    }

    [Test]
    public void Plan_InvalidBitOrder_Throws()
    {
        var serializer = CreateSerializer(Json(ThreeFields, 3, """, "bitorder": "sideways" """));

        Assert.That(() => serializer.GetPlan(MsgName), Throws.InstanceOf<ExecutionPlanException>());
    }

    [Test]
    public void Plan_BitPackerOnAnOrdinaryGroup_Throws()
    {
        const string schema = """
            { "name": "Doc", "members": [
              { "name": "Msg", "kind": "group",
                "processors": [ { "name": "sys.bitpacker" } ],
                "members": [ { "name": "A", "kind": "field", "datatype": "sys.uint8" } ] }
            ] }
            """;
        var serializer = CreateSerializer(schema);

        Assert.That(() => serializer.GetPlan(MsgName), Throws.InstanceOf<ExecutionPlanException>());
    }

    // Flat model: one instance serves as source and sink, values are keyed by schema path and repeat instance.
    private sealed class Model : IValueSource, IValueSink
    {
        public Dictionary<string, object?> Values { get; } = [];

        public void Set(string path, object value) => Values[path] = value;

        public SourceResult GetField(FieldContext context)
            => Values.TryGetValue($"{context.Path}{context.Instance}", out var value)
                ? SourceResult.Provided(new LogicalField(context.Name, value!.GetType(), value))
                : SourceResult.NoValue();

        public void SetField(FieldContext context, LogicalField value) => Values[$"{context.Path}{context.Instance}"] = value.Value;

        public int GetCount(RepeatContext context) => -1;
        public int GetSelectedIndex(ChoiceContext context) => throw new NotSupportedException();

        IValueSource IValueSource.EnterGroup(GroupContext context) => this;
        IValueSource IValueSource.EnterItem(RepeatContext context, int index) => this;
        IValueSource IValueSource.EnterChoice(ChoiceContext context) => this;
        IValueSink IValueSink.EnterGroup(GroupContext context) => this;
        IValueSink IValueSink.EnterItem(RepeatContext context, int index, int count) => this;
        IValueSink IValueSink.EnterChoice(ChoiceContext context, int selectedIndex) => this;
        public void ExitGroup(GroupContext context) { }
        public void ExitItem(RepeatContext context, int index, int count) { }
        public void ExitChoice(ChoiceContext context, int selectedIndex) { }
    }
}
