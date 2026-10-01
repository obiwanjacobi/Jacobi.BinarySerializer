# Binary Serializer

- **Schema-driven**
  - The schema is the source of truth.
  - It describes logical fields and physical/layout behavior.

- **Two-layer model**
  - **Logical/semantic transforms**: scale, enum mapping, nullability, value normalization.
  - **Physical/layout transforms**: bit packing, endian conversion, alignment, framing.

- **Codec selection must be explicit**
  - Properties alone are not enough.
  - A field/node should identify the **exact codec** (or codec family) to use.
  - Properties configure that codec.

- **Open extensibility**
  - Schema nodes should have an open property bag.
  - New codecs can introduce new properties/concepts.
  - Unknown properties can be ignored unless a codec claims them.

- **Composable pipelines**
  - A node may pass through multiple stages/codecs.
  - Order should be deterministic, not guessed.
  - Best model: fixed stages with ordered codecs inside each stage.

  Stream
    -> Segment A pipeline
    -> Segment B pipeline
    -> Segment C pipeline

- **Stateful layout codecs**
  - Some codecs span multiple logical values.
  - They maintain running state and flush when a pattern/unit is full.
  - This is needed for bit packing and other grouped physical layouts.

  Logical values
    -> layout accumulator state
    -> when pattern full / boundary reached
    -> emit physical block
    -> next layer

- **Segmented streams**
  - One stream can contain different layout rules in different regions.
  - The schema should define boundaries/segments so different pipelines can apply to different parts.

- **Decode/encode symmetry**
  - Every codec should ideally have both directions.
  - The runtime should be able to map physical bytes back to logical values using the same schema.

Recommendations:
- **explicit codec ID**
- **open property bag**
- **fixed pipeline stages**
- **stateful segment/layout codecs**
- **schema-defined boundaries**

---

Schema
  |
  v
Schema Binder / Resolver
  - reads node type
  - resolves explicit codec ID
  - reads codec properties
  - builds ordered pipeline
  |
  v
Pipeline Executor
  |
  +--> Stage 1: Semantic codec(s)
  |       - scale
  |       - enum map
  |       - null handling
  |
  +--> Stage 2: Representation codec(s)
  |       - varint
  |       - fixed-width
  |       - custom field transform
  |
  +--> Stage 3: Layout codec(s)
  |       - endian swap
  |       - bit pack
  |       - alignment
  |       - multi-value accumulation
  |
  +--> Stage 4: Segment / stream codec(s)
          - framing
          - checksum
          - compression
          - encryption

---

Example Schema in json:

```json
{
  "name": "TelemetryPacket",
  "kind": "group",
  "pipeline": ["root"],
  "children": [
    {
      "name": "Header",
      "kind": "group",
      "pipeline": ["byteAligned"],
      "children": [
        { "name": "MessageType", "kind": "field", "type": "u8", "codec": "identity" },
        { "name": "Version", "kind": "field", "type": "u8", "codec": "identity" }
      ]
    },
    {
      "name": "Flags",
      "kind": "group",
      "pipeline": ["bitPacked"],
      "children": [
        { "name": "IsActive", "kind": "field", "type": "bool", "codec": "bit", "bit": 0 },
        { "name": "IsTrusted", "kind": "field", "type": "bool", "codec": "bit", "bit": 1 },
        { "name": "HasError", "kind": "field", "type": "bool", "codec": "bit", "bit": 2 }
      ]
    },
    {
      "name": "Samples",
      "kind": "repeat",
      "count": { "codec": "fieldRef", "field": "SampleCount" },
      "children": [
        {
          "name": "Sample",
          "kind": "group",
          "pipeline": ["byteAligned"],
          "children": [
            { "name": "Timestamp", "kind": "field", "type": "u32", "codec": "identity", "endian": "little" },
            { "name": "Value", "kind": "field", "type": "i16", "codec": "scaled", "scale": 10 }
          ]
        }
      ]
    }
  ]
}

```

---

Here’s a clean C#-style shape for it.

```csharp
public enum PipelineStage
{
    Semantic,
    Representation,
    Layout,
    Stream
}

public interface ICodec
{
    string Id { get; }
    PipelineStage Stage { get; }

    bool CanEncode(Type logicalType, CodecContext context);
    bool CanDecode(Type logicalType, CodecContext context);
}

// Semantic pipeline stage: logical value transforms (scale, enum mapping, nullability, etc.)
public interface IValueCodec : ICodec
{
    object Encode(object logicalValue, CodecContext context);
    object Decode(object physicalValue, CodecContext context);
}

// Representation pipeline stage: physical representation transforms (varint, fixed-width, etc.)
public interface IFieldCodec : ICodec
{
    EncodedField EncodeField(in LogicalField field, CodecContext context);
    LogicalField DecodeField(in EncodedField field, CodecContext context);
}

// Layout pipeline stage: bit packing, alignment, endian conversion, etc.
public interface ILayoutCodec : ICodec
{
    void BeginWrite(ref LayoutWriter writer, CodecContext context);
    void WriteValue(ref LayoutWriter writer, in LogicalField field, CodecContext context);
    void EndWrite(ref LayoutWriter writer, CodecContext context);

    void BeginRead(ref LayoutReader reader, CodecContext context);
    bool TryReadValue(ref LayoutReader reader, out LogicalField field, CodecContext context);
    void EndRead(ref LayoutReader reader, CodecContext context);
}

// Stream pipeline stage: framing, compression, encryption, etc.
public interface IStreamCodec : ICodec
{
    void Encode(ReadOnlySpan<byte> input, IBufferWriter<byte> output, CodecContext context);
    void Decode(ReadOnlySpan<byte> input, IBufferWriter<byte> output, CodecContext context);
}

public sealed class LogicalField
{
    public required string Name { get; init; }
    public required Type LogicalType { get; init; }
    public required object? Value { get; init; }
}

public sealed class EncodedField
{
    public required string Name { get; init; }
    public required int BitWidth { get; init; }
    public required ulong RawBits { get; init; }
}

public sealed class CodecContext
{
    public required IReadOnlyDictionary<string, object> Properties { get; init; }
    public required SchemaNode SchemaNode { get; init; }
    public required IServiceProvider Services { get; init; }
}
```

A schema node could describe which codec to use:

```csharp
public sealed class SchemaNode
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public required string CodecId { get; init; }
    public required IReadOnlyDictionary<string, object> Properties { get; init; }
    public required IReadOnlyList<SchemaNode> Children { get; init; }
}
```

Then the pipeline API might look like this:

```csharp
public interface IPipeline
{
    byte[] Encode<T>(T value, SchemaNode schema);
    T Decode<T>(ReadOnlySpan<byte> data, SchemaNode schema);
}
```

Or, to make the logical values explicit, use a field-based API:

```csharp
public interface ISerializer
{
    void WriteRecord<TRecord>(TRecord logicalRecord, SchemaNode schema, IBufferWriter<byte> output);
    TRecord ReadRecord<TRecord>(ReadOnlySpan<byte> input, SchemaNode schema);
}
```

A more explicit version that clearly names the logical inputs:

```csharp
public interface IRecordWriter
{
    void WriteLogicalValue(string fieldName, object logicalValue);
    void WriteLogicalField(in LogicalField field);
    void WriteGroup(string groupName, IReadOnlyList<LogicalField> fields);
}
```

Example usage:

```csharp
var record = new Telemetry
{
    MessageType = 1,
    Version = 2,
    IsActive = true,
    IsTrusted = false,
    Temperature = 21.35m
};

serializer.WriteRecord(record, telemetrySchema, buffer);
```

And field-level usage:

```csharp
writer.WriteLogicalField(new LogicalField
{
    Name = "IsActive",
    LogicalType = typeof(bool),
    Value = true
});
```

My recommendation:
- **`LogicalField` / `LogicalRecord`** for the API surface
- **`ICodec`** for selection metadata
- **`ILayoutCodec`** for stateful packing
- **`IStreamCodec`** for whole-stream transforms
- **`SchemaNode`** to bind logical names to codec IDs

---

Yes — a good alternative is a **schema-shaped logical document API** instead of a POCO/record API.

A clean shape is:

```csharp
public interface ILogicalNode
{
    SchemaNode SchemaNode { get; }
}

public interface ILogicalValueNode : ILogicalNode
{
    object? Value { get; }
}

public interface ILogicalGroupNode : ILogicalNode
{
    IReadOnlyList<ILogicalNode> Children { get; }
}

public interface ILogicalArrayNode : ILogicalNode
{
    IReadOnlyList<ILogicalNode> Items { get; }
}
```

A field node would carry both the logical value and the schema node that describes it:

```csharp
public sealed class LogicalValueNode : ILogicalValueNode
{
    public required SchemaNode SchemaNode { get; init; }
    public required object? Value { get; init; }
}
```

A group/record node:

```csharp
public sealed class LogicalGroupNode : ILogicalGroupNode
{
    public required SchemaNode SchemaNode { get; init; }
    public required IReadOnlyList<ILogicalNode> Children { get; init; }
}
```

A repeated field / array node:

```csharp
public sealed class LogicalArrayNode : ILogicalArrayNode
{
    public required SchemaNode SchemaNode { get; init; }
    public required IReadOnlyList<ILogicalNode> Items { get; init; }
}
```

Then the API can be schema-driven:

```csharp
public interface ISchemaSerializer
{
    void Write(ILogicalNode node, IBufferWriter<byte> output);
    ILogicalNode Read(SchemaNode schema, ReadOnlySpan<byte> input);
}
```

Example usage:

```csharp
var telemetryNode = new LogicalGroupNode
{
    SchemaNode = telemetrySchema,
    Children =
    [
        new LogicalValueNode
        {
            SchemaNode = telemetrySchema.Fields["MessageType"],
            Value = (byte)1
        },
        new LogicalValueNode
        {
            SchemaNode = telemetrySchema.Fields["Version"],
            Value = (byte)2
        },
        new LogicalGroupNode
        {
            SchemaNode = telemetrySchema.Groups["Flags"],
            Children =
            [
                new LogicalValueNode
                {
                    SchemaNode = telemetrySchema.Groups["Flags"].Fields["IsActive"],
                    Value = true
                },
                new LogicalValueNode
                {
                    SchemaNode = telemetrySchema.Groups["Flags"].Fields["IsTrusted"],
                    Value = false
                }
            ]
        }
    ]
};

serializer.Write(telemetryNode, output);
```

This gives you:

- no dependency on a pre-existing CLR class
- direct alignment with schema structure
- field-level metadata available at every node
- support for mixed nested groups, arrays, and repeated records
