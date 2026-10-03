# Binary Serializer

## TODOs

- [ ] Fix Processor Property dependencies in the Schema.

### Runtime engine

- [x] **Repeat and choice support.** Done in `PlanCursor`, `ReaderSession` and `WriterSession`: a repeat visits its children `Count` times (`EnterItem`), a choice only the selected alternative (`EnterChoice`). Counts and indexes resolve from constants or the published-values store; reading an unpublished value is a runtime error. `SessionState.Publish` is public so the developer can prefill counts before writing.
  - Remaining (see the items below): processor state per iteration, `SchemaValueRef` instance indices.
- [x] **Layout per repeat item.** Layout `Begin`/`End` runs once per item (not once per repeat), in both sessions.
- [x] **Instance indices in the layout context.** `LayoutProcessorContext.Instance` holds the repeat indices for both the group and the field being laid out.
- [ ] **Repeat stream processors.** Stream processors on a repeat group should run once per repeat (around all items), not per item. The cursor already has that point (the repeat's EnterGroup/ExitGroup); builds on the non-root stream processor item below.
- [ ] **Processor private state per iteration.** State keyed per binding is shared across repeat iterations; may be wrong for stateful processors.
- [ ] **Detect repeat count mismatches at schema compile time.** A constant count that cannot match the model/referenced count (schema out of sync) should be reported when the plan is built, not only at write time (currently an `InvalidOperationException`).
- [ ] **Instance indices in `SchemaValueRef`.** A path reference to a field inside a repeat is ambiguous (which iteration?). For now the latest published value overwrites earlier ones. Extend the reference syntax with instance indices, e.g. `root.grp[2].fld`; a "current item" form may be needed for references from inside the same iteration.
- [x] **Instance path type.** `InstancePath` (repeat indices, outermost first) is tracked by `PlanCursor.Instance` and exposed as `Instance` on the value-model contexts (`FieldContext`, `GroupContext`, `RepeatContext`, `ChoiceContext`), so flat models can tell iterations apart.
- [x] **Repeat instance indices in the processor context.** All processor contexts (value, field, layout, stream) expose `Instance`; the session sets it before each call (for a repeat item before its layout Begin). Values published inside a repeat still overwrite each other until `SchemaValueRef` supports indices.
- [x] **Schema references to published values.** (Indices inside repeats: see `SchemaValueRef` item above.) A repeat count
- [x] **Ranges that start inside a repeat.** `ExecutionPlan.CreateRange(fromPath, fromInstance, toPath, toInstance)` takes an `InstancePath` per bound; the cursor skips repeat items outside the range. Missing indices mean the first (from) or last (to) item.
- [x] **Flat API with repeats and choices.** `IFieldSource` / `IFieldSink` no longer need repeat counts or choice indexes: the engine resolves them from constants or published values, and flat models get `Instance` in the contexts. (The writer's count-vs-model check is skipped for flat models.)
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
- [x] **Constant `Count` / `SelectedIndex` reported as unresolved.** In `SchemaSet.ResolveReferences` a literal count or selected index falls into the `_ => false` case, so a valid schema is reported as having unresolved references.
- [ ] **Typed repeats and choices lose their type.** `InstantiateType` checks `SchemaGroup` before `SchemaRepeat` / `SchemaChoice`, so a repeat or choice that uses a typedef is instantiated as a plain group.
- [ ] **Field typedef processors replace instead of merge.** When a field references a typedef, the typedef's processors replace the field's own processors. They should be merged, with the field's own processors taking precedence.
- [ ] **Complex schema property values.** `SchemaProperty.Value` is a single string that the processor interprets. Allow richer values, e.g. lists or object structures (`SchemaNode.cs`).

