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
| `Publish(ns, name, value)` | Publishes a public value. |
| `Field` / `Group` | The node being processed (varies by stage). Layout also has `RootPosition`/`GroupPosition`. |

## Properties

- A processor declares the properties it supports (`Properties`). The schema supplies the values per use of the processor, as strings.
- `SchemaSet.Compile` expands the short names to `namespace.id.name`; the lookup via `context.Properties` accepts the short name, so a processor just asks for `"scale"`.
- The processor parses and validates the value itself (and fails with a clear message). Required properties that are missing are a failure.
- Properties of the use override those of a processor definition (`processorDefs`), see [Schema](../Schema/readme.md).
- Values are constants. Reading a value from a `ref:`/`pub:` in a property is not supported yet.

## State

Because processors are stateless, anything that must survive between calls goes into `context.GetOrCreateState<T>()`:

- `StateScope.Binding` (default): one instance per processor use in the plan, shared by all repeat items.
- `StateScope.Instance`: one instance per repeat instance (`context.Instance`).

State is private: a processor cannot read the state of another one. State lives for one session (one message).

## Publishing values

`context.Publish(ns, name, value)` makes a value public for the rest of the session, for instance a length that was read. The namespace is usually configurable with the `pubns` property so several uses of a processor do not collide. Schema nodes read it with `pub:ns.name`. A later publication of the same key overwrites the earlier one.


---

- [ ] define a list of global/well-known properties (names and datatypes).
- [ ] 

## Well-Known Properties

A common set of properties that are used by the mechanism or other processors. 

| Property Name | Data Type | Description |
|---------------|-----------|-------------|
| pubns | string | Publish Namespace: the namespace used when a processor publishes public values. |
| length | uint | The length of the data being processed. String with a fixed length can be encoded this way. |

## Specifying Processor Properties

In general the processor key is to be used as a prefix to the property name when specifying properties for a processor.

`sys:enum.map` - where `sys:enum` is the processor key and `map` is the property name.

However when specifying properties inside the processor-definitions in a schema, the properties are alread listed under the processor key, so the prefix is not needed.

## Publishing Public Values

A processor can publish public values that can be used by other components in the pipeline.
These values can be used to configure the processor or to provide information about the processing that has been done.

A public value can be published using a namespace and a name. 
The namespace is used to group related values together and prevents collisions between multiple processors publishing the same value.
The name is used to identify the value within the namespace.

Typically the namespace can be set on the publishing processor.