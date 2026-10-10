# TODOs

- [ ] **TypeDef for Groups include members** Should the typedef for groups also include members? And how do these members merge with the members of the group that references the typedef?
- [ ] **Freeze `SchemaSet` and `ProcessorProvider` once handed to a `Serializer`.** Cached `ExecutionPlan`s go stale if a schema is loaded/recompiled or a processor is registered after `SerializerBuilder.Build()`. Fix: add an `IsFrozen`/`Freeze()` to `SchemaSet` (after `Compile`) and to the processor provider (`ProcessorManager`), call it in `Build()`, and throw `InvalidOperationException` on later modification. Add tests.
  For `SchemaSet` use a builder pattern: `SchemaSetBuilder` loads then `Build()` (Compile) returns a frozen `SchemaSet`.
- [ ] **Write API documentation for the public API** including some examples. Describe what services are supported and expected.
- [ ] **Processer pipeline stage processing should add all parent (group) processors** in order when running them. A child (group) that defines a processor for a specific stage does NOT replace it's parent processors for that stage, but adds to the pipeline. The current implementation replaces the parent processors for that stage with the child processors. Or do we have to make it selectable/overridable per stage? Or is this covered by the fact that different stages related to Fields and Groups? What about nested groups?
- [ ] **Complex schema property values.** `PropertyDescriptor.DataType` now names a registered data type (`DataTypeDescriptor`, with a parser), so a processor can declare typed properties (including custom types such as `my.point`) and the string in `SchemaProperty.Value` is parsed by that type instead of by the processor itself. The plan builder already validates presence of required properties and that each value parses (`ExecutionPlanBuilder.ValidateProperties`).
  - [ ] the value is still a single string in the schema model (`SchemaNode.cs`), so lists and nested objects need a parser-defined text syntax; decide whether to allow structured JSON/XML values (list/object) that are handed to the data type parser. 
  - [ ] report unknown property names (typos) and value constraints (ranges) at plan build.
  - [ ] allow value references (`ref:`/`pub:`) in complex property values (the engine currently only resolves them for simple string values).
- [ ] **Data type follow-ups.** Composite/structured data types and a neutral value tree for processor property types; enum data types for the remaining string-valued properties (align `relative`, varint `encoding`); built-in typedefs; replacing a registered descriptor; unit tests for the `Descriptors` namespace.
- [ ] **Size on groups: follow-ups.** 
  - [x] `byteSize` (constant or `ref:`/`pub:`) on any group/repeat/choice is done (see below). 
  - [x] a variable-width (varint) derived size field: the size is encoded at group exit into its own buffer and flushed before the group content, so it takes the width its value needs.
  - [ ] a variable-width derived size field in a group that contains layout processors (e.g. align) fails when the width differs from the assumed one (the probe of size 0); estimate a common width from the schema, or retry with ascending widths (beware oscillation); 
  - [ ] size value processors; a size field that comes after its group is not derived; 
  - [x] bit-level layouts/positions inside the deferred region; the probe encode of the size field runs twice.
  - [ ] size on a root and overlapping size regions; the reader checks the window only at group exit/open-repeat end (a field crossing the window is caught at exit).
- [ ] **ReaderSession: consolidate group stacks.** `_groupStarts` (layout group start, pushed in `BeginLayout`, popped in `EndLayout`, never for repeats) and `_windows` (size window end, pushed at group entry, popped at group exit, repeats included) are popped at different moments, so one stack needs a record per group (`Start`, `End?`, `HasLayout`) pushed at EnterGroup and popped at ExitGroup, with `GroupPosition` reading the nearest layout record. Same for the writer's `_sizeStarts`.
  - [ ] Add: window end/remaining to the groupData.
- [ ] **Map processor follow-ups.** `sys:map` maps only scalar types (the `logical` property selects the logical type) 
  - [x] reparses its properties on every call. Use state in context to cache the parsed map.
- [ ] **Bare JSON `value` literals.** `SchemaField.Value` is a string union, so a bare JSON number or boolean (`"value": 42`) is not supported; only strings (e.g. `"0x2A"`).
- [ ] **Allow processor Read/Write to optionally skip.** Add a result option for a processor to skip processing and let the engine perform a pass-through. TBD: skip-self and/or skip-stage?
- [ ] A `byteLength` given as a reference on a fixed-size field without a field processor is not checked against the type size at plan build (only fails when read/written).
- [ ] **MIDI schema gaps** (see `IntegrationTests/Midi/Midi.xml`): 
  - [ ] running status (a first byte below 0x80 reuses the previous status); 
  - [ ] the status peek must be supplied by the model on write (not derived from the chosen alternative); 
  - [ ] the repeated `Status` group cannot be reused (typedef/group reuse); no test parses `Midi.xml` yet. See item on Group TypeDef to include members.

---

- [x] **Tests for field `Value`.** Parsing, JSON/XML round-trip, reader mismatch/match, writer derivation and mismatch, `ref:`/`pub:` values, typedef instantiation.
- [x] **CRC follow-ups.** 
  - [x] Missing processor-level tests (corrupt CRC, truncated input, bad properties, other algorithms); 
  - [x] no `poly`/`init` and `xorout` validation of custom parameters beyond the width.
- [x] **String: use the field-level `ByteLength`.** 
  - [x] Done for constant lengths: `sys:string` uses `FieldData.ByteLength` (in bytes, same as `Bytes`) and the string data type supports a length; the processor property `byteLength` still works as a fallback (both together is an error). 
  - [x] tests for `ref:`/`pub:` field lengths on strings, deriving a length field from a string value on write (`ByteLengthOf` only handles `byte[]`), and deciding whether to drop the `byteLength` property. Characters-based lengths are not supported (the encoded width depends on the encoding).
- [x] **String follow-ups.** 
  - [x] `byteLength` as `ref:`/`pub:` (processor property values are constants only, see complex property values), 
  - [x] publishing the detected length
  - [x] a byteLength prefix. Would that not simply be passing a field ref to the byteLength property?
- [x] **Publish namespace (`pubns`).** An optional property on the Schema type (not an engine-interpreted processor property) to set the namespace a processor publishes its values under, so published values do not collide.
- [x] **SchemaField Dummy** to allow filler/dummy/don't-care fields in the schema. The engine currently requires a field to have a data type and a value model property. Or have literal fields (with a constant value) not trigger logical model events and have a serializer setting to turn that off?
- [x] **Mark a SchemaField as 'hidden'** Such a field will be processed as defined but not appear in the logical model(s) (IValueSource/IValueSink, IFieldSource/IFieldSink). This is useful for fields that are required for the binary format but not relevant to the logical model (e.g. a CRC or a reserved field).
- [x] **Unknown processors throw from the builder.** `ExecutionPlanBuilder.Bind` throws for an unknown processor namespace or id instead of adding an error to `ExecutionPlanException`, so a schema with several problems reports only the first. Fix: use `TryCreateProcessor` and report it with the node path.
- [x] **Running data on the contexts (`FieldData`, `GroupData`).**
  - [x] `FieldData.Length` and `GroupData.Size` exist (`Processor/NodeData.cs`; on the layout context and the field context, `GroupData` on the layout context only; the writer only knows the group size at `EndWrite`). 
  - [x] Positions (`GroupData.RootPosition`/`Position`), `FieldData.OpenWidthWindowBytes`, and `GroupData.RepeatIndex`/`RepeatCount`/`ChoiceIndex` are moved/added.
- [x] **Derive length prefixes [--and choice discriminators--].** Lengths that precede the data they measure: buffer the group, then write the length field (see the back-patch optimization below). Not needed: --Choice discriminators (the selected index) derived from the model are last.--
- [x] **Map default entry.** A `sys:map` property with a null value is the default: reading an unmapped physical value returns the default's logical value; writing the default's logical value throws (there is no physical value). Used for unknown PNG chunk types.
- [x] **Nullable property values.** `SchemaProperty.Value` is `string?` (JSON `null`, XML `xsi:nil`, stored as a `nil` attribute on `property` elements).
- [x] **CRC layout processor.** `sys:crc` (head or chained follower) appends the CRC of the group's bytes on write and validates it on read; no field represents the CRC. `CrcCodec` is a generic table-driven CRC (width 1-64, Rocksoft parameters) with named presets (crc32, crc32c, crc16-*, crc8-*, crc24-openpgp, crc64-*); properties `algorithm`, `byteorder` and per-parameter overrides.
- [x] **Generic data types.** `SchemaDataType` is a name-based struct (short names normalize to `sys.<lowercase>`, case-insensitive; optional types are `SchemaDataType?`, no `None`). A per-serializer `DataTypeRegistry` (`IDataTypeRegistry` read-only view for processors via `ProcessorContext.DataTypes`) maps names to `DataTypeDescriptor`s (CLR type, literal parser, optional encode/decode, fixed size, length support). Built-ins (`sys.string`, `int8`..`int64`, `uint8`..`uint64`, `boolean`, `double`, `datetime`, `bytes`, `object`) are prepopulated descriptors (`BuiltInDataTypes`); custom types use the same mechanism. The engine has no fallback: an unregistered type is a plan-build error.
- [x] **SourceResult.** `IFieldSource`/`IValueSource` use `SourceResult GetField(FieldContext)` (Value, NoValue = engine derives or fails, EndOfData = no more items of a count-less repeat; elsewhere an error). Flat sources can now write count-less repeats.
- [x] **Optimize the serialization formats for more consise property definition.** Allow properties to be defined as format-native properties instead of listing them under 'properties' with a 'name' and 'value'.
- [x] **Repeat until end.** A repeat without a count runs until the end of the input (last node of its group only).
**Bytes data type and field `ByteLength`.** `sys.bytes` is a raw
- [x] **Size on groups.** `byteSize` on a group, repeat or choice bounds its encoded content in bytes (refers to the content, not the size field itself). The reader opens a window (content must end exactly at it; too little input is NeedMoreData; an open repeat stops at the window end). The writer derives the size field by encoding the content first, or checks a declared size against the measured one. Open-ended (count-less) nodes must be last, recursively through choices and plain groups, unless the group is sized.
- [x] **API: Add `Assembly` overloads**: to `LoadAssembly` in `SchemaSet` and `LoadFromAssembly` in `ProcessorManager`.
- [X] **API: Allow `IProcessorFactory` through `IServiceProvider`**: Perhaps bypass the `ProcessManager` entirely. Implement a `IProcessorProvider` over `IServiceProvider` (`ProcessorProvider`).
- [x] **API: Add `Serializer` root object** as a container for all dependencies. `SerializerBuilder` configures; `Serializer` is immutable and caches `ExecutionPlan`s per schema name (`GetPlan`/`Prepare`/`PrepareAll`).
- [x] **Value processors on repeat/choice.** `valueProcessors` on a repeat or choice (separate from the layout/stream `processors`) convert the referenced count/index value to an int (read direction, e.g. `sys.map` over a string field).
- [x] **Derive values the model does not hold.** Done: a field that a sibling repeat's `Count` refers to is derived from `IValueSource.GetCount` when the model has no value (flat `IFieldSource` models must be explicit).
- [x] **String and variable-width fields.** `sys:string` field processor: `encoding` (default UTF-8), fixed `byteLength` (padded with `padding`, trimmed on read) or single-byte `terminator`.
- [x] **Processor diagnostics**. The engine currently has no logging. Add a `ILogger` to the sessions and processor context, and log the plan node path and instance indices for each processor call.
- [x] **Private processor state key.** Processor state in `SessionState` is keyed by `ProcessorBinding`. A TODO notes it may need extra key data to tell two processors of the same type apart (e.g. the same processor bound at different nodes).
- [x] **Processor private state per iteration.** State keyed per binding is shared across repeat iterations.
- [x] **Instance indices in node refs.** `ref:root.grp[2].fld` targets a specific repeat item; `ref:root.grp[].fld` means the same instance as the referrer; no index means the first item. Only `ref:` node refs take indices (not `pub:`). Values are published per instance; an unpublished instance is a runtime error.
- [x] **Schema Processor Def Properties.** Done: `SchemaSet.Compile` expands short property names to `ns.id.name`, also for aliases (`ref:alias` / `ref:doc.alias`), using the key of the resolved def. Ref properties override def properties.
- [x] **Processor def/ref split.** `SchemaProcessorDef` (named, in `ProcessorDefs`) and `SchemaProcessorRef` (key or `ref:[doc.]alias`, on nodes and typedefs) in the schema and in the JSON/XML models. Processors on typedefs and roots are resolved at compile time.
- [x] **More engine tests.** Done in `EngineEdgeCaseTests`: truncated varint through the engine (mid-stream cut), consumed-bits mismatch (throws), and the `FieldWriteResult` failure path.
- [x] **Field-level layout Begin/End semantics.** Layout `BeginWrite`/`EndWrite` and `BeginRead`/`EndRead` are only called per group. Decide whether fields that declare their own layout processors also get Begin/End calls, and what that means (e.g. a bit-packed field). No: Begin/End are per group, not per field. The layout processor is responsible for any field-level state it needs.
- [x] **Repeat and choice support.** Done in `PlanCursor`, `ReaderSession` and `WriterSession`: a repeat visits its members `Count` times (`EnterItem`), a choice only the selected alternative (`EnterChoice`). Counts and indexes resolve from constants or the published-values store; reading an unpublished value is a runtime error. `SessionState.Publish` is public so the developer can prefill counts before writing.
- [x] **Layout per repeat item.** Layout `Begin`/`End` runs once per item (not once per repeat), in both sessions.
- [x] **Instance indices in the layout context.** `LayoutProcessorContext.Instance` holds the repeat indices for both the group and the field being laid out.
- [x] **Instance path type.** `InstancePath` (repeat indices, outermost first) is tracked by `PlanCursor.Instance` and exposed as `Instance` on the value-model contexts (`FieldContext`, `GroupContext`, `RepeatContext`, `ChoiceContext`), so flat models can tell iterations apart.
- [x] **Repeat instance indices in the processor context.** All processor contexts (value, field, layout, stream) expose `Instance`; the session sets it before each call (for a repeat item before its layout Begin). Values published inside a repeat are published per instance.
- [x] **Schema references to published values.** (Indices inside repeats: see `SchemaValueRef` item above.) A repeat count
- [x] **Ranges that start inside a repeat.** `ExecutionPlan.CreateRange(fromPath, fromInstance, toPath, toInstance)` takes an `InstancePath` per bound; the cursor skips repeat items outside the range. Missing indices mean the first (from) or last (to) item.
- [x] **Flat API with repeats and choices.** `IFieldSource` / `IFieldSink` no longer need repeat counts or choice indexes: the engine resolves them from constants or published values, and flat models get `Instance` in the contexts. (The writer's count-vs-model check is skipped for flat models.)
- [x] **Multiple Representation (field) processors per field.** Done: same-signature followers (`IFieldWriter<EncodedField,EncodedField>` / `IFieldReader<EncodedField,EncodedField>`) chain after the head `IFieldProcessor`; the plan builder validates chains (`ValidateFieldChain`). Original note: Only one is allowed; more throws `NotSupportedException`.
- [x] **Virtual fields (`byteOffset`).** A field with a `byteOffset` is read at that offset from the current position and the position is restored; the model sees the value on read and must supply it on write (no bytes written). Its value can be referenced (e.g. a choice `selectedIndex` peeking at a status byte).
- [x] **`sys.bits` (semantic).** Extracts a bit range (`bitoffset`, `bitlength`) from an integer value, with sign extension for signed types. Combine with a virtual field to get the high/low nibble of a peeked byte.
- [x] **Sized repeat without a count.** A `repeat` with `byteSize` and no `count` repeats until the end of the window; the size is derived on write. Tests in `SessionSizedGroupTests`.
- [x] **`sys.bitslicer` and `BitSlicerCodec`.** Joins the low `bits` bits (1-8, default 7) of each byte of one field (MIDI 14-bit pitch bend, sync-safe integers). The byte count is the field `byteLength` or the `bytelength` property.
- [x] **`byteLength` on all fixed-size data types except `Boolean`.** The data type is the logical representation, so the physical length is a property of the field. Without a field processor a constant length must equal the type size (plan error otherwise).
- [x] **Multiple Layout processors per field.** Done: every layout processor is a head; followers also implement `ILayoutWriter<ReadOnlySpan<byte>>` / `ILayoutReader<ReadOnlyMemory<byte>>` and the engine owns the buffers between stages (`ValidateLayoutChain`, `sys:align` is the first follower). Original note: Same limit: only one layout processor can write/read a field.
- [x] **Variable-length integers (`sys:varint`).** Field processor with an `encoding` property: `leb128` (default, unsigned), `sleb128`, `zigzag`, `vlq` (MIDI) and `prefix` (UTF-8 style length prefix). One static codec class per algorithm in `Codecs`. Signed field types need `sleb128` or `zigzag`; `vlq` and `prefix` are unsigned only.
- [x] **Open-width fields and result types.** `IFieldReader.Read` returns `FieldReadResult<T>` (status, value, bits consumed) and `IFieldWriter.Write` returns `FieldWriteResult<T>` (status, value, bits written); layout reads return `LayoutReadResult<T>`. With field processors, the default layout offers a window of up to `OpenWidthWindowBytes` (10) bytes and the engine rewinds the unused bytes. Layout writes and stream stages still return `WriteResult` / `ReadResult`.
- [x] **Typed repeats and choices lose their type.** `InstantiateType` checks `SchemaGroup` before `SchemaRepeat` / `SchemaChoice`, so a repeat or choice that uses a typedef is instantiated as a plain group.
- [x] **Field typedef processors replace instead of merge.** When a field references a typedef, the typedef's processors replace the field's own processors. They should be merged, with the field's own processors taking precedence.
- [x] **Constant `Count` / `SelectedIndex` reported as unresolved.** In `SchemaSet.ResolveReferences` a literal count or selected index falls into the `_ => false` case, so a valid schema is reported as having unresolved references.

## Nice to Have

- [ ] **Detect repeat count mismatches at schema compile time.** A constant count that cannot match the model/referenced count (schema out of sync) should be reported when the plan is built, not only at write time (currently an `InvalidOperationException`).
- [ ] **Optimization: back-patch forward-referenced values when the output is seekable.** Values that depend on later output (e.g. a byte length written before its string) are first handled by buffering the group; with a seekable writer the slot could be reserved and patched instead.
- [ ] **Stream processors on non-root groups.** Only the root group's stream processors run (framing, compression, encryption of the whole message). A nested group with its own stream processors (e.g. an encrypted sub-block) is not supported, but must not become impossible.
- [ ] **Repeat stream processors.** Stream processors on a repeat group should run once per repeat (around all items), not per item. The cursor already has that point (the repeat's EnterGroup/ExitGroup); builds on the non-root stream processor item below.
- [ ] **Typed-object API.** Where interfacing is done through client-defined POCOs, not by implementing interfaces.
- [ ] **EndOfData probe: layout side effects.** The writer ends a count-less repeat for a flat source by probing the first field of the next item (`SourceResult.EndOfData`) before the item is entered; the probe only covers a direct first field (not a first field inside a nested repeat or choice). Layout processors may have side-effects if the item/group were entered before the probe; revisit if that shows up.
- [ ] **String follow-ups.** 
  - [ ] multi-byte terminators for UTF-16/32
  - [ ] reporting property errors at plan build instead of at read time. A field processor that needs more data than the open-width window (10 bytes) is retried by the reader with a doubled window while more input is available.
- [ ] **CRC follow-ups.** require engine changes:
  - [ ] a read error is thrown (no NeedMoreData) because `EndRead` has no result
  - [ ] the CRC covers the whole group only (no sub-range or exclusion) (nive to have/not doing)

## Not Doing These

- [-] **API: Allow `Stream` and `byte[]` for both input and output.** The engine currently requires `IBinaryWriter` and `SequenceReader<byte>`. Do we create adapters, or add `Stream` overloads to the engine and the processors?
- [-] **(De)Serialize overloads for all variations** Plan|Range, `IValueSource`|`IFieldSource`, `Stream`|`byte[]`|`IBinaryWriter` and `IValueSink`|`IFieldSink`, `Stream`|`byte[]`|`SequenceReader<byte>`.
- [-] **Bytes follow-ups.** Length value processors; a length field that comes after its bytes field is not derived; `Bytes` is not supported by bit/byte-level layouts or field processors that assume fixed widths; no streaming/chunked access for large blobs (the model gets one `byte[]` per field); a flat model needs an explicit `byte[]` value.
- [ ] **Expected-value stage.** A field `Value` (constant/ref) is compared against the logical value (after the semantic stage). Make the stage explicitly selectable (default logical).
