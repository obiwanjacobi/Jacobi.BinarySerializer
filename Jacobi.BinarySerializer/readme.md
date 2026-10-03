# Binary Serializer

## TODOs

- [ ] Fix Processor Property dependencies in the Schema.

### Runtime engine

- [ ] **Repeat and choice support.** `PlanCursor` throws `NotSupportedException` for repeat and choice nodes, so neither session can handle them. A repeat must visit its children `Count` times (via `IValueSource.EnterItem` / `IValueSink.EnterItem`). A choice must visit only the selected alternative (`EnterChoice`). Needs the item below first.
The schema definition for these references may need to change. Also needs a public place where processors can publish such data; reading an unpublished value is always an error (detected at runtime).
Track it in the cursor frames when repeat is implemented.
- [ ] **Repeat instance indices in the processor context.** The processor context (not only `FieldContext`) should expose the repeat instance indices too. Until then, values published inside a repeat overwrite each other.
- [ ] **Schema references to published values.** A repeat count / choice index can reference a value by `pubns/val` (published by a processor) or directly by schema path. For a path reference the builder resolves the target field at build time (unknown path or target after the referencing node = build error) and flags it so the session stores its value in the published-values store; both forms then resolve through the same store lookup (unpublished = runtime error). With repeats, a path reference needs the instance indices to pick the right iteration.
- [ ] **Ranges that start inside a repeat.** The repeat indices belong in the `PlanRange` (not only in `FieldContext`): a range bound is a `SchemaPath` plus an index per repeated node on the way. `PlanRange` currently works for ranges outside repeats; needs the instance path.
- [ ] **Flat API with repeats and choices.** `IFieldSource` / `IFieldSink` (see `FieldModel.cs`) currently throw for repeat counts and choice indexes; they must come from published or derived values.
- [ ] **Derive values the model does not hold.** Lengths, counts and discriminators have no property in the user's model. The writer currently throws "the value model has no value for the field". The engine must compute them (e.g. count = number of items written) and write them.
- [ ] **String and variable-width fields.** `DataTypeCodec` has no String support, and the default layout read passes all remaining bytes on as one value. Strings need a length, terminator or length-prefix, taken from schema properties on the field.
- [ ] **Multiple Representation (field) processors per field.** Only one is allowed; more throws `NotSupportedException`. They cannot be chained because the type changes (`LogicalField` to `EncodedField`). Needs a rule for how several combine.
- [ ] **Multiple Layout processors per field.** Same limit: only one layout processor can write/read a field. Needs a rule for how several share the same buffer.
- [ ] **Private processor state key.** Processor state in `SessionState` is keyed by `ProcessorBinding`. A TODO notes it may need extra key data to tell two processors of the same type apart (e.g. the same processor bound at different nodes).
- [ ] **Resume a read after `NeedMoreData`.** When input is short, the caller currently reads again from the start with more data. The cursor and frame stack were designed to be resumable, so the session could continue where it stopped.
- [ ] **Stream processors on non-root groups.** Only the root group's stream processors run (framing, compression, encryption of the whole message). A nested group with its own stream processors (e.g. an encrypted sub-block) is not supported, but must not become impossible.
- [ ] **Field-level layout Begin/End semantics.** Layout `BeginWrite`/`EndWrite` and `BeginRead`/`EndRead` are only called per group. Decide whether fields that declare their own layout processors also get Begin/End calls, and what that means (e.g. a bit-packed field).
- [ ] **Typed-object API.**
- [x] **`ProcessorPipeline.cs` TODO.** Stale comment that only restates the order (write: value, field, layout, stream; read: reverse). Both sessions implement it, so the comment can be deleted.

### Known issues

- [ ] **Unknown processors throw from the builder.** `ExecutionPlanBuilder.Bind` throws for an unknown processor namespace or id instead of adding an error to `ExecutionPlanException`, so a schema with several problems reports only the first. Fix: use `TryCreateProcessor` and report it with the node path.
- [ ] **Constant `Count` / `SelectedIndex` reported as unresolved.** In `SchemaSet.ResolveReferences` a literal count or selected index falls into the `_ => false` case, so a valid schema is reported as having unresolved references.
- [ ] **Typed repeats and choices lose their type.** `InstantiateType` checks `SchemaGroup` before `SchemaRepeat` / `SchemaChoice`, so a repeat or choice that uses a typedef is instantiated as a plain group.
- [ ] **Field typedef processors replace instead of merge.** When a field references a typedef, the typedef's processors replace the field's own processors. They should be merged, with the field's own processors taking precedence.
- [ ] **Complex schema property values.** `SchemaProperty.Value` is a single string that the processor interprets. Allow richer values, e.g. lists or object structures (`SchemaNode.cs`).

