# Processor

An object that converts/translates data from one format to another.

Processors hold the logic the schema deliberately does not: scaling, enum mapping, varints, alignment, checksums, compression, etc. A schema selects them by key and configures them with properties; the [engine](../Execution/readme.md) calls them.

## Pipeline stages

Each processor belongs to exactly one stage (`IProcessor.Stage`). The `ProcessorPipeline` sorts the processors of a node into the stages; a child node reuses the stages of its parent unless it specifies processors for that stage.

| Stage | Interface | Converts | Called |
|-------|-----------|----------|--------|
| Value | `IValueProcessor` | logical value <-> encoded value (scaled, mapped, compressed) | Per field. Reports the bits written/consumed. |
| Field | `IFieldProcessor` | logical value <-> encoded field (varint, string) | Per field. Reports the bits written/consumed. |
| Layout | `ILayoutProcessor` | encoded field <-> bytes in a group (bit packing, alignment, byte order, CRC) | `BeginWrite/Read` when the group is entered, `Write/Read` per encoded field, `EndWrite/Read` when the group is left. |
| Stream | `IStreamProcessor` | whole message payload <-> transport bytes (framing, compression, encryption) | Once per message, on the root only. |

Order per field:

```
write: value -> field -> layout -> (stream, whole message) -> output
read:  input -> (stream, whole message) -> layout -> field -> value
```

- Several processors in one stage form a **chain** in declaration order. Value processors run in declaration order on write and in reverse on read. Layout declaration order is outermost to innermost: the innermost processor handles encoded fields, while enclosing processors also implement the byte-to-byte interfaces. Writes flow inner-to-outer; reads produce bytes outer-to-inner on demand. A read wrapper consumes all input used to produce its returned output; input and output positions are accounted for independently. Chains span nested groups, with ancestor declarations before local declarations. Each group calls begin/end only for its own declarations: begins outer-to-inner and ends inner-to-outer. Inherited processors remain active without repeating their hooks at descendant scopes. The plan builder validates the required roles.
- A stage without processors is skipped; the engine uses a default (fixed-width codec, bytes appended as is).
- The engine owns the buffers between the stages.

### Buffered layout processors

Layout processors can consume or produce zero physical bytes while completing a field. The engine never interprets bit widths or offsets. `sys.bitpacker`, for example, is an innermost layout processor on an ordinary group; it retains a partial byte between fields and pads at group exit. Fields still follow the normal value and representation pipelines.

On write, empty inner output does not call enclosing byte wrappers. Output produced by `EndWrite` is forwarded through those wrappers before their own end hooks run. A buffering endpoint can declare `context.ByteBlockSize` in both begin hooks to define logical physical-block boundaries independent of field calls; writes and reads use matching blocks. These boundaries are not input-window sizes. Byte wrappers see their physical block width through `FixedByteLength` and the requested output length through `InputByteLength`. `IsReadContinuation` distinguishes a refill of the same field or hook from a new operation, so alignment is not repeated when a window grows.

On read, every stage sees its currently buffered input, possibly empty. `NeedMoreData(minimumBytes)` requests a minimum total unread input count; omitting the count lets the engine choose a window (10 bytes initially when the size is unknown, then growing). The engine obtains enclosing-stage output and retries through an explicit frame stack. NeedMoreData must leave processor-private state unchanged; trial reader positions are committed only on success. A successful byte wrapper consumes all input used to produce its returned output; unused output remains in the child's input buffer. BeginRead and EndRead return the status-only LayoutReadResult record struct, so one hook implementation serves both field and byte-wrapper reader interfaces. Read returns the separate LayoutReadResult<T> record struct with its output value. Both result types use the same refill mechanism; the scheduler copies status and requested byte count while keeping output separately typed. The engine propagates NeedMoreData to the caller only when bounded input cannot complete the operation. There is no cache-only mode or separate input-request result.

Open-width representation reads grow their encoded window incrementally within one field operation, avoiding layout/root-reader rewinds and speculative consumption of the next field. A processor sets `HasBufferedInput` while it retains private unread input so an open repeat does not stop early; after ending a nested scope, the hint must describe the resumed parent scope.

Physical consumption is measured from the reader and output writer, independently of the canonical encoded field's `BitWidth`. A field reconstructed from cached bits can therefore return a normal encoded value while consuming zero additional physical bytes. An open repeat checks retained input before stopping at physical EOF; where trailing bits are padding rather than values, specify the repeat count.

Group hooks have `Field = null` and cleared field data. Empty groups do not invoke layout hooks. Open choices still resolve their runtime body. No special packed node, packing stage, logical-value gather/scatter, or whole-group carrier is used.

| Stage | Read Interface | Write Interface | Description |
|-------|----------------|-----------------|-------------|
| Value | `IValueReader<LogicalField, LogicalField>` | `IValueWriter<LogicalField, LogicalField>` | Does not require any special implementation, these interfaces are implemented by IValueProcessor. |
| Field | `IFieldReader<EncodedField, EncodedField>` | `IFieldWriter<EncodedField, EncodedField>` | Allows the field processor to be chained after the 'head' processor. |
| Layout | `ILayoutReader<ReadOnlyMemory<byte>>` | `ILayoutWriter<ReadOnlySpan<byte>>` | Required for layout processors that wrap an inner processor. |
| Stream | - | - | Not available yet. |

## Implementing a processor

1. Pick the stage and implement the matching interface (`IValueProcessor`, `IFieldProcessor`, `ILayoutProcessor` or `IStreamProcessor`). `ScaleProcessor` (value), `VarIntProcessor` (field) and `AlignProcessor` (layout) are good templates.
2. Provide the identity members: `Key` (`namespace.id`, e.g. `sys.scale`), `Name` (for logging), `Stage` and `Properties` (the `PropertyDescriptor`s: name, type, required, read-only, published).
3. Implement both directions (`Write` and `Read`). Return the result types: `FieldWriteResult`/`FieldReadResult` (value + bits), `LayoutReadResult`, or `WriteResult`/`ReadResult` for stream processors. Use `NeedMoreData` when the input is truncated and `Failure` for data that is invalid.
4. Register it in an `IProcessorFactory` (one factory per namespace; `CreateProcessor(id)`), and register the factory with the `ProcessorManager` (`Register` or `LoadFromAssembly`). Built-in processors are listed in `ProcessorFactory` of `Jacobi.BinarySerializer.Processors`.
5. Add tests: round trip, truncated input and bad property values.

Rules:

- **Stateless.** One instance serves all sessions and nodes. Never keep fields that change per message or per node.
- **No processor-to-processor references.** Share data through published values or by putting an algorithm in a static codec (`Codecs/`).
- **Do not add interfaces to a processor** to satisfy an engine need; the engine/contexts are extended instead.
- Report problems through `context.Logger.Fail(...)` (throws with the node path) rather than your own exceptions.

## The context

Every call gets a context for its stage (`ValueProcessorContext`, `FieldProcessorContext`, `LayoutProcessorContext`, `StreamProcessorContext`). The session sets it up before each call. Common members:

| Member | Use |
|--------|-----|
| `Properties` | Lookup of this processor's own properties (`Find(name)`, `Get(name)`). |
| `PropertiesOf(...)` | Lookup over other property lists (field/group properties) from the point of view of this processor. |
| `Services` | The host's `IServiceProvider`. |
| `Logger` | Logger for this processor; a no-op without a logger factory. |
| `Instance` | The repeat instance indices of the current node. |
| `GetOrCreateState<T>(scope)` | Private per-session state (see below). |
| `Publish(name, value)` | Publishes a public value. The namespace is the processor key (or the schema `pubns`), so the processor passes only the name. |
| `Field` / `Group` | The node being processed (varies by stage). Layout also has `RootPosition`/`GroupPosition`. Root position is the layout-input offset; group position is measured in the current processor's byte view, relative to its declaration scope. |

### Reserving a tail (layout, read only)

A layout processor that reads trailing bytes in `EndRead` (a checksum, an end marker such as the SysEx `0xF7`) calls `context.ReserveTail(byteCount)` from `BeginRead`. Reservations of all active layouts add up and are released when that group's `EndRead` has run.

- A repeat without a count (read until the end) and without a size window stops before the reserved bytes, so the trailing bytes are left for `EndRead`. A repeat also stops when fewer bytes remain than the minimum encoded size of one item (the fixed sizes of its members; unknown sizes count as 0, and at least 1 byte is always required).
- A group or repeat with a `byteSize` is not affected: its window is exact and already excludes the trailing bytes.
- Calling `ReserveTail` anywhere else (any write hook, `Read`, `EndRead`) throws `InvalidOperationException`. When writing nothing needs reserving: the item count comes from the model and `EndWrite` appends the trailing bytes after the group's content.

## Properties

- A processor declares the properties it supports (`Properties`). The schema supplies the values per use of the processor, as strings.
- `SchemaSet.Compile` expands the short names to `namespace.id.name`; the lookup via `context.Properties` accepts the short name, so a processor just asks for `"scale"`.
- The processor parses and validates the value itself (and fails with a clear message). Required properties that are missing are a failure.
- Properties of the use override those of a processor definition (`processorDefs`), see [Schema](../Schema/readme.md).
- Values are constants. Reading a value from a `ref:`/`pub:` in a property is not supported yet.
- Each property is described by a `PropertyDescriptor` (`Name`, `DataType`, `IsRequired`, ...). `DataType` is the name of a registered data type (e.g. `sys.int32`, or the enum type `sys.endianness`), the same mechanism as schema field types. Processors keep their descriptors in `static readonly` fields and read values with `context.Properties`:
  - `Get<T>(descriptor)` throws when the property is absent (a 'required' message if `IsRequired`, otherwise 'not found') or when the value is unparsable or not a `T`.
  - `GetOrDefault<T>(descriptor, default)` returns the default when absent, but throws when absent and `IsRequired` (or when the value is invalid).
  - `TryGet<T>(descriptor, out value)` never throws: false when absent, invalid or not a `T`.
  - The plan builder validates at plan build that required properties are present and that supplied values parse with the declared data type.
  - Enum data types are created with `DataTypeDescriptor.ForEnum<T>(name)` (case-insensitive member names).

## Data types

`context.DataTypes` is a read-only `IDataTypeRegistry`: processors look up `DataTypeDescriptor`s (parse, encode/decode, CLR type) by name but cannot register or change types while processing. Registration happens on the `DataTypeRegistry` handed to the `SerializerBuilder`.

## State

Because processors are stateless, anything that must survive between calls goes into `context.GetOrCreateState<T>()`:

- `StateScope.Binding` (default): one instance per processor use in the plan, shared by all repeat items.
- `StateScope.Instance`: one instance per repeat instance (`context.Instance`).

State is private: a processor cannot read the state of another one. State lives for one session (one message).

## Publishing values

`context.Publish(ns, name, value)` makes a value public for the rest of the session, for instance a length that was read. The namespace is usually configurable with the `pubns` property so several uses of a processor do not collide. Schema nodes read it with `pub:ns.name`. A later publication of the same key overwrites the earlier one.


---

## Well-Known Properties

These properties are **managed by the engine**: they are defined by the schema (on the node), resolved by the engine (constants, `ref:` and `pub:` values are looked up for you) and handed to the processor through the context. A processor can **read** them but does not declare, parse or validate them as its own properties, and cannot change them.

| Name | Schema member | Data Type | Where a processor reads it | Description |
|------|---------------|-----------|----------------------------|-------------|
| `byteLength` | field `byteLength` | int | `context.FieldData.ByteLength` (field and layout contexts) | The resolved physical length in bytes of the field (not a character or item count). Null when the field declares none. A processor may also offer its own `bytelength` property as an alternative (`sys:string`, `sys:bitslicer`); giving both is an error. |
| `byteSize` | group `byteSize` | int | `context.GroupData.ByteSize` (layout context) | The resolved size in bytes of the group content. When reading it is known at group start; when writing it is only known at group end. |
| `pubns` | processor entry `pubns` | string | Applied by `context.Publish` | The namespace the processor publishes under (default: the processor key). |

## Specifying Processor Properties

In general the processor key is to be used as a prefix to the property name when specifying properties for a processor.

`sys:varint.encoding` - where `sys:varint` is the processor key and `encoding` is the property name.

However when specifying properties inside the processor-definitions in a schema, the properties are alread listed under the processor key, so the prefix is not needed.

## Publishing Public Values

A processor can publish public values that other parts of the schema can use (e.g. as a `byteLength`, `count` or `value`), or that give information about the processing that was done.

Publishing takes only a name: `context.Publish(name, value)`. The engine supplies the namespace, so the processor cannot collide with others by accident:

- the namespace is the processor key (`sys.crc`), unless the schema entry sets `pubns`; then that is used instead (use it when the same processor appears more than once);
- the full name of a published value is `namespace.name` (`sys.crc.value`) and a schema refers to it with `pub:` (no instance indices; the last published value wins).

Declare each published value in the processor's `Properties` as a read-only, published `PropertyDescriptor` (`isReadOnly: true, isPublished: true`), so it is documented and the plan can check references to it. Examples: `sys.crc` publishes `value` (the calculated CRC), `sys.string` publishes `length` (the string length in chars).

The values of fields are published by the engine itself under their schema path, for `ref:`; a processor does not publish those.
