# Binary Serializer

## API

How to setup and interface with the binary serializer.

```csharp
// load the schema definitions of the binary formats you wish to serialize.
SchemaSet schemas = new();
schemas.LoadFile(".json|.xml|.yml|.yaml");
schemas.LoadFromAssembly(Asembly|"*.dll|*.exe");
...
schemas.Compile(); // resolve references, validate

// load the processors (code) that perform transformation and other logic.
ProcessorManager processors = new();
processors.LoadFromAssembly(Assembly|"*.dll|*.exe");
processors.Register(IProcessorFactory);

// serialize will ask you for logical values
IValueSource valueSource = ...
// deserialize will give you logical values
IValueSink valueSink = ...

var outputStream = byte[]|Stream|IBinaryWriter;
var inputStream = byte[]|Stream|SequenceReader<byte>;

// available to processors
IServiceProvider services = ...;

// bring it together in the serializer (immutable, thread-safe, owns the ExecutionPlan cache)
Serializer serializer = new SerializerBuilder()
    .AddSchemas(schemas)
    .AddProcessors(processors) // or .AddServices(services)
    .AddServices(services)
    .Build();
ExecutionPlan plan = serializer.GetPlan("schemaName"); // cached

// write logical to binary
var result = serializer.Serialize(plan, IValueSource|IFieldSource, outputStream, services);
// result == NeedMoreSpace?

// read binary to logical
var result = serializer.Deserialize(plan, IValueSink|IFieldSink, inputStream, services);
// result == NeedMoreData?
```

## TODOs

- [ ] **Freeze `SchemaSet` and `ProcessorProvider` once handed to a `Serializer`.** Cached `ExecutionPlan`s go stale if a schema is loaded/recompiled or a processor is registered after `SerializerBuilder.Build()`. Fix: add an `IsFrozen`/`Freeze()` to `SchemaSet` (after `Compile`) and to the processor provider (`ProcessorManager`), call it in `Build()`, and throw `InvalidOperationException` on later modification. Add tests.
- [ ] **API: Allow `Stream` and `byte[]` for both input and output.** The engine currently requires `IBinaryWriter` and `SequenceReader<byte>`. Do we create adapters, or add `Stream` overloads to the engine and the processors?
- [ ] **(De)Serialize overloads for all variations** Plan|Range, `IValueSource`|`IFieldSource`, `Stream`|`byte[]`|`IBinaryWriter` and `IValueSink`|`IFieldSink`, `Stream`|`byte[]`|`SequenceReader<byte>`.
- [ ] **Write API documentation for the public API** including some examples. Describe what services are supported and expected.
- [ ] **Detect repeat count mismatches at schema compile time.** A constant count that cannot match the model/referenced count (schema out of sync) should be reported when the plan is built, not only at write time (currently an `InvalidOperationException`).
- [ ] **Derive length prefixes and choice discriminators.** Lengths that precede the data they measure: buffer the group, then write the length field (see the back-patch optimization below). Choice discriminators (the selected index) derived from the model are last.
- [ ] **String follow-ups.** `length` as `ref:`/`pub:` (processor property values are constants only, see complex property values), publishing the detected length, a length prefix (see above), multi-byte terminators for UTF-16/32, and reporting property errors at plan build instead of at read time. A field processor that needs more data than the open-width window (10 bytes) is retried by the reader with a doubled window while more input is available.
- [ ] **Optimization: back-patch forward-referenced values when the output is seekable.** Values that depend on later output (e.g. a byte length written before its string) are first handled by buffering the group; with a seekable writer the slot could be reserved and patched instead.
- [ ] **Stream processors on non-root groups.** Only the root group's stream processors run (framing, compression, encryption of the whole message). A nested group with its own stream processors (e.g. an encrypted sub-block) is not supported, but must not become impossible.
- [ ] **Unknown processors throw from the builder.** `ExecutionPlanBuilder.Bind` throws for an unknown processor namespace or id instead of adding an error to `ExecutionPlanException`, so a schema with several problems reports only the first. Fix: use `TryCreateProcessor` and report it with the node path.
- [ ] **Repeat stream processors.** Stream processors on a repeat group should run once per repeat (around all items), not per item. The cursor already has that point (the repeat's EnterGroup/ExitGroup); builds on the non-root stream processor item below.
- [ ] **SchemaField Dummy** to allow filler/dummy/don't-care fields in the schema. The engine currently requires a field to have a data type and a value model property.
- [ ] **Complex schema property values.** `SchemaProperty.Value` is a single string that the processor interprets. Allow richer values, e.g. lists or object structures (`SchemaNode.cs`).
- [ ] **Typed-object API.** Where interfacing is done through client-defined POCOs, not by implementing interfaces.
- [ ] **String: use the field-level `Length`.** `SchemaField.Length` now exists (for `Bytes`); look into using it for `sys:string` instead of the processor property `length`. Open: `Length` is logical (characters) for strings but bytes for `Bytes`; the encoded width then depends on the encoding. Terminators and padding stay in the processor.
- [ ] **Bytes follow-ups.** Length value processors; a length field that comes after its bytes field is not derived; `Bytes` is not supported by bit/byte-level layouts or field processors that assume fixed widths; no streaming/chunked access for large blobs (the model gets one `byte[]` per field); a flat model needs an explicit `byte[]` value.
- [ ] **Size on groups: follow-ups.** `size` (constant or `ref:`/`pub:`) on any group/repeat/choice is done (see below). Open: size value processors; a variable-width (varint) derived size field (the width must not change after the content is known); a size field that comes after its group is not derived; bit-level layouts/positions inside the deferred region; the probe encode of the size field runs twice (publishes the placeholder); size on a root and overlapping size regions; the reader checks the window only at group exit/open-repeat end (a field crossing the window is caught at exit).
- [ ] **EndOfData probe: layout side effects.** The writer ends a count-less repeat for a flat source by probing the first field of the next item (`SourceResult.EndOfData`) before the item is entered; the probe only covers a direct first field (not a first field inside a nested repeat or choice). Layout processors may have side effects if the item/group were entered before the probe; revisit if that shows up.
- [x] **SourceResult.** `IFieldSource`/`IValueSource` use `SourceResult GetField(FieldContext)` (Value, NoValue = engine derives or fails, EndOfData = no more items of a count-less repeat; elsewhere an error). Flat sources can now write count-less repeats.
- [ ] **Publish namespace (`pubns`).** An optional property on the Schema type (not an engine-interpreted processor property) to set the namespace a processor publishes its values under, so published values do not collide.
- [ ] **ReaderSession: consolidate group stacks.** `_groupStarts` (layout group start, pushed in `BeginLayout`, popped in `EndLayout`, never for repeats) and `_windows` (size window end, pushed at group entry, popped at group exit, repeats included) are popped at different moments, so one stack needs a record per group (`Start`, `End?`, `HasLayout`) pushed at EnterGroup and popped at ExitGroup, with `GroupPosition` reading the nearest layout record. Same for the writer's `_sizeStarts`.
- [x] **Map default entry.** A `sys:map` property with a null value is the default: reading an unmapped physical value returns the default's logical value; writing the default's logical value throws (there is no physical value). Used for unknown PNG chunk types.
- [x] **Nullable property values.** `SchemaProperty.Value` is `string?` (JSON `null`, XML `xsi:nil`, stored as a `nil` attribute on `property` elements).
- [x] **CRC layout processor.** `sys:crc` (head or chained follower) appends the CRC of the group's bytes on write and validates it on read; no field represents the CRC. `CrcCodec` is a generic table-driven CRC (width 1-64, Rocksoft parameters) with named presets (crc32, crc32c, crc16-*, crc8-*, crc24-openpgp, crc64-*); properties `algorithm`, `byteorder` and per-parameter overrides.
- [ ] **CRC follow-ups.** Missing processor-level tests (corrupt CRC, truncated input, bad properties, other algorithms); a read error is thrown (no NeedMoreData) because `EndRead` has no result; the CRC covers the whole group only (no sub-range or exclusion); no `refin`/`refout` validation of custom parameters beyond the width.
- [ ] **Map processor follow-ups.** `sys:map` maps only scalar types (the `logical` property selects the logical type) and reparses its properties on every call.
- [ ] **Expected-value stage.** A field `Value` (constant/ref) is compared against the logical value (after the semantic stage). Make the stage explicitly selectable (default logical).
- [ ] **Suppress constants in the reader.** The sink currently still receives constant-valued fields via `SetField`. Add an option to skip them.
- [ ] **Bare JSON `value` literals.** `SchemaField.Value` is a string union, so a bare JSON number or boolean (`"value": 42`) is not supported; only strings (e.g. `"0x2A"`).
- [ ] **Rethink `SchemaDataType`.** Under reconsideration; constant literal parsing (`DataTypeCodec.TryParse`) depends on it.
- [ ] **Tests for field `Value`.** Parsing, JSON/XML round-trip, reader mismatch/match, writer derivation and mismatch, `ref:`/`pub:` values, typedef instantiation.
- [ ] **Allow processor Read/Write to optionally skip.** Add a result option for a processor to skip processing and let the engine perform a pass-through. TBD: skip-self and/or skip-stage?

- [x] **Repeat until end.** A repeat without a count runs until the end of the input (last node of its group only).
- [x] **Bytes data type and field `Length`.** `SchemaDataType.Bytes` is a raw `byte[]` (default encoding is a raw copy, no processor needed). `SchemaField.Length` (constant, `ref:` or `pub:`; JSON `length`, XML `length` attribute/element) gives the number of bytes; it is only valid on `Bytes`. Without a length the field takes the rest of the enclosing sized group, so it needs a window and must be the last node (plan-build errors otherwise). The writer derives a field that a length refers to from the `byte[]` length, or checks a declared length. Constants are hex literals (`0x8950`, whitespace allowed).
- [x] **Size on groups.** `size` on a group, repeat or choice bounds its encoded content in bytes (refers to the content, not the size field itself). The reader opens a window (content must end exactly at it; too little input is NeedMoreData; an open repeat stops at the window end). The writer derives the size field by encoding the content first, or checks a declared size against the measured one. Open-ended (count-less) nodes must be last, recursively through choices and plain groups, unless the group is sized.
- [x] **API: Add `Assembly` overloads**: to `LoadAssembly` in `SchemaSet` and `LoadFromAssembly` in `ProcessorManager`.
- [X] **API: Allow `IProcessorFactory` through `IServiceProvider`**: Perhaps bypass the `ProcessManager` entirely. Implement a `IProcessorProvider` over `IServiceProvider` (`ProcessorProvider`).
- [x] **API: Add `Serializer` root object** as a container for all dependencies. `SerializerBuilder` configures; `Serializer` is immutable and caches `ExecutionPlan`s per schema name (`GetPlan`/`Prepare`/`PrepareAll`).
- [x] **Value processors on repeat/choice.** `valueProcessors` on a repeat or choice (separate from the layout/stream `processors`) convert the referenced count/index value to an int (read direction, e.g. `sys.map` over a string field).
- [x] **Derive values the model does not hold.** Done: a field that a sibling repeat's `Count` refers to is derived from `IValueSource.GetCount` when the model has no value (flat `IFieldSource` models must be explicit).
- [x] **String and variable-width fields.** `sys:string` field processor: `encoding` (default UTF-8), fixed `length` (padded with `padding`, trimmed on read) or single-byte `terminator`.
- [x] **Processor diagnostics**. The engine currently has no logging. Add a `ILogger` to the sessions and processor context, and log the plan node path and instance indices for each processor call.
- [x] **Private processor state key.** Processor state in `SessionState` is keyed by `ProcessorBinding`. A TODO notes it may need extra key data to tell two processors of the same type apart (e.g. the same processor bound at different nodes).
- [x] **Processor private state per iteration.** State keyed per binding is shared across repeat iterations.
- [x] **Instance indices in node refs.** `ref:root.grp[2].fld` targets a specific repeat item; `ref:root.grp[].fld` means the same instance as the referrer; no index means the first item. Only `ref:` node refs take indices (not `pub:`). Values are published per instance; an unpublished instance is a runtime error.
- [x] **Schema Processor Def Properties.** Done: `SchemaSet.Compile` expands short property names to `ns.id.name`, also for aliases (`ref:alias` / `ref:doc.alias`), using the key of the resolved def. Ref properties override def properties.
- [x] **Processor def/ref split.** `SchemaProcessorDef` (named, in `ProcessorDefs`) and `SchemaProcessorRef` (key or `ref:[doc.]alias`, on nodes and typedefs) in the schema and in the JSON/XML models. Processors on typedefs and roots are resolved at compile time.
- [x] **More engine tests.** Done in `EngineEdgeCaseTests`: truncated varint through the engine (mid-stream cut), consumed-bits mismatch (throws), and the `FieldWriteResult` failure path.
- [x] **Field-level layout Begin/End semantics.** Layout `BeginWrite`/`EndWrite` and `BeginRead`/`EndRead` are only called per group. Decide whether fields that declare their own layout processors also get Begin/End calls, and what that means (e.g. a bit-packed field). No: Begin/End are per group, not per field. The layout processor is responsible for any field-level state it needs.
- [x] **Repeat and choice support.** Done in `PlanCursor`, `ReaderSession` and `WriterSession`: a repeat visits its children `Count` times (`EnterItem`), a choice only the selected alternative (`EnterChoice`). Counts and indexes resolve from constants or the published-values store; reading an unpublished value is a runtime error. `SessionState.Publish` is public so the developer can prefill counts before writing.
- [x] **Layout per repeat item.** Layout `Begin`/`End` runs once per item (not once per repeat), in both sessions.
- [x] **Instance indices in the layout context.** `LayoutProcessorContext.Instance` holds the repeat indices for both the group and the field being laid out.
- [x] **Instance path type.** `InstancePath` (repeat indices, outermost first) is tracked by `PlanCursor.Instance` and exposed as `Instance` on the value-model contexts (`FieldContext`, `GroupContext`, `RepeatContext`, `ChoiceContext`), so flat models can tell iterations apart.
- [x] **Repeat instance indices in the processor context.** All processor contexts (value, field, layout, stream) expose `Instance`; the session sets it before each call (for a repeat item before its layout Begin). Values published inside a repeat are published per instance.
- [x] **Schema references to published values.** (Indices inside repeats: see `SchemaValueRef` item above.) A repeat count
- [x] **Ranges that start inside a repeat.** `ExecutionPlan.CreateRange(fromPath, fromInstance, toPath, toInstance)` takes an `InstancePath` per bound; the cursor skips repeat items outside the range. Missing indices mean the first (from) or last (to) item.
- [x] **Flat API with repeats and choices.** `IFieldSource` / `IFieldSink` no longer need repeat counts or choice indexes: the engine resolves them from constants or published values, and flat models get `Instance` in the contexts. (The writer's count-vs-model check is skipped for flat models.)
- [x] **Multiple Representation (field) processors per field.** Done: same-signature followers (`IFieldWriter<EncodedField,EncodedField>` / `IFieldReader<EncodedField,EncodedField>`) chain after the head `IFieldProcessor`; the plan builder validates chains (`ValidateFieldChain`). Original note: Only one is allowed; more throws `NotSupportedException`.
- [x] **Multiple Layout processors per field.** Done: every layout processor is a head; followers also implement `ILayoutWriter<ReadOnlySpan<byte>>` / `ILayoutReader<ReadOnlyMemory<byte>>` and the engine owns the buffers between stages (`ValidateLayoutChain`, `sys:align` is the first follower). Original note: Same limit: only one layout processor can write/read a field.
- [x] **Variable-length integers (`sys:varint`).** Field processor with an `encoding` property: `leb128` (default, unsigned), `sleb128`, `zigzag`, `vlq` (MIDI) and `prefix` (UTF-8 style length prefix). One static codec class per algorithm in `Codecs`. Signed field types need `sleb128` or `zigzag`; `vlq` and `prefix` are unsigned only.
- [x] **Open-width fields and result types.** `IFieldReader.Read` returns `FieldReadResult<T>` (status, value, bits consumed) and `IFieldWriter.Write` returns `FieldWriteResult<T>` (status, value, bits written); layout reads return `LayoutReadResult<T>`. With field processors, the default layout offers a window of up to `OpenWidthWindowBytes` (10) bytes and the engine rewinds the unused bytes. Layout writes and stream stages still return `WriteResult` / `ReadResult`.
- [x] **Typed repeats and choices lose their type.** `InstantiateType` checks `SchemaGroup` before `SchemaRepeat` / `SchemaChoice`, so a repeat or choice that uses a typedef is instantiated as a plain group.
- [x] **Field typedef processors replace instead of merge.** When a field references a typedef, the typedef's processors replace the field's own processors. They should be merged, with the field's own processors taking precedence.
- [x] **Constant `Count` / `SelectedIndex` reported as unresolved.** In `SchemaSet.ResolveReferences` a literal count or selected index falls into the `_ => false` case, so a valid schema is reported as having unresolved references.
