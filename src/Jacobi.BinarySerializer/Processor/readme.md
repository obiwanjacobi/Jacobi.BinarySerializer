# Processor

An object that converts/translates data from one format to another.

Processors hold the logic the schema deliberately does not: scaling, enum mapping, varints, alignment, checksums, compression, etc. A schema selects them by key and configures them with properties; the [engine](../Execution/readme.md) calls them.

## Pipeline stages

Each processor belongs to exactly one stage (`IProcessor.Stage`). The `ProcessorPipeline` sorts the processors of a node into the stages; a child node reuses the stages of its parent unless it specifies processors for that stage.

| Stage | Interface | Converts | Called |
|-------|-----------|----------|--------|
| Semantic (value) | `IValueProcessor` | logical value <-> logical value (scale, enum, nullable, map) | Per field; also for the count/index of a repeat/choice (`valueProcessors`, read direction only). |
| Representation (field) | `IFieldProcessor` | logical value <-> encoded field (varint, string) | Per field. Reports the bits written/consumed. |
| Layout | `ILayoutProcessor` | encoded field <-> bytes in a group (packing, alignment, crc) | `BeginWrite/Read` when the group is entered, `Write/Read` per field, `EndWrite/Read` when the group is left. |
| Stream | `IStreamProcessor` | whole message payload <-> transport bytes (framing, compression, encryption) | Once per message, on the root only. |

Order per field:

```
write: value -> field -> layout -> (stream, whole message) -> output
read:  input -> (stream, whole message) -> layout -> field -> value
```

- Several processors in one stage form a **chain** in declaration order. Value processors are applied in order when writing; the reverse is the engine's concern when reading. For layout chains the first is the head (field <-> bytes) and followers work byte-to-byte; the last one talks to the actual stream. The plan builder validates chains.
- A stage without processors is skipped; the engine uses a default (fixed-width codec, bytes appended as is).
- The engine owns the buffers between the stages.

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
| `Field` / `Group` | The node being processed (varies by stage). Layout also has `RootPosition`/`GroupPosition`. |

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

`sys:enum.map` - where `sys:enum` is the processor key and `map` is the property name.

However when specifying properties inside the processor-definitions in a schema, the properties are alread listed under the processor key, so the prefix is not needed.

## Publishing Public Values

A processor can publish public values that other parts of the schema can use (e.g. as a `byteLength`, `count` or `value`), or that give information about the processing that was done.

Publishing takes only a name: `context.Publish(name, value)`. The engine supplies the namespace, so the processor cannot collide with others by accident:

- the namespace is the processor key (`sys.crc`), unless the schema entry sets `pubns`; then that is used instead (use it when the same processor appears more than once);
- the full name of a published value is `namespace.name` (`sys.crc.value`) and a schema refers to it with `pub:` (no instance indices; the last published value wins).

Declare each published value in the processor's `Properties` as a read-only, published `PropertyDescriptor` (`isReadOnly: true, isPublished: true`), so it is documented and the plan can check references to it. Examples: `sys.crc` publishes `value` (the calculated CRC), `sys.string` publishes `length` (the string length in chars).

The values of fields are published by the engine itself under their schema path, for `ref:`; a processor does not publish those.
