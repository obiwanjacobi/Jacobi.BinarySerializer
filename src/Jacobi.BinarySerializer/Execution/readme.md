# Execution

The engine: turns a compiled schema into an `ExecutionPlan` and walks that plan to write a value model to bytes or read bytes into a value model.

## Overview

```
Schema (JSON/XML) --SchemaSet.Compile--> compiled schema --ExecutionPlanBuilder--> ExecutionPlan (immutable)

write:  IValueSource -> Semantic -> Representation -> Layout -> (Stream, root only) -> bytes
read:   bytes -> (Stream, root only) -> Layout -> Representation -> Semantic -> IValueSink
```

| Part | Role |
|------|------|
| `ExecutionPlan` | Immutable tree of `NodeInfo` (`GroupInfo`, `RepeatInfo`, `ChoiceInfo`, `FieldInfo`). Each node has a path, its resolved processor pipeline and its bound references. Plans are cached by the `Serializer`. |
| `PlanCursor<TScope>` | Walks the plan with an explicit frame stack and emits steps (`EnterGroup`, `Field`, `ExitGroup`, `EnterItem`, `ExitItem`, `Done`). Model-agnostic: the caller decides what a scope is. |
| `WriterSession` / `ReaderSession` | Drive the cursor for one message. Call the processors, own the buffers and positions, resolve derived values. One session per message, not thread-safe. |
| `SessionState` | Per-session state shared with processors: private processor state, published values, logging. |
| `NodeInfo` / `InstancePath` | Identify a node (schema path) and one occurrence of it inside repeats (`[2]`, `[1][0]`). |
| `PlanRange` | Restricts a session to a range of fields (and the groups leading to them). |
| `FieldAdapters` | Wrap the flat `IFieldSource`/`IFieldSink` so the engine only ever sees the structured interfaces. |

## How it works

1. **Plan building.** The builder walks the compiled schema, binds processors (the `ProcessorPipeline` sorts them into stages), binds constants and references (`ref:`, `pub:`) and validates processor chains. All problems found are reported together in an `ExecutionPlanException`.
2. **Stepping.** A session creates a `PlanCursor` and loops on `Next()`. The cursor only tells *what* comes next; the session decides *how* (create a scope, process a field, resolve a count).
3. **Scopes.** On `EnterGroup`/`EnterItem` the session asks the value model for a child scope and hands it to the cursor (`Enter`, `EnterRepeat`, `EnterOpenRepeat`). Fields are then read from/written to the scope of their containing group.
4. **Fields.** Per field the session runs the stages in order (see the diagram). A stage without processors is short-circuited: the value passes unchanged, is encoded fixed-width by the default codec, or the bytes go straight to the output.
5. **Finishing.** The root's stream processors (if any) run over the whole payload: after layout when writing, before layout when reading.

## Rules

- **Values only through `IValueSource` (write) and `IValueSink` (read).** The engine never touches a concrete model. This keeps a future typed-object API possible without changing the engine.
- **No call-stack state.** Everything that is "where are we" lives on the cursor's frame stack and the session's stacks, so a walk is resumable and nothing recurses.
- **Nodes are identified by plan `NodeInfo` (Name/Path)**, occurrences by `InstancePath`. Not by model member names.
- **Derived values are resolved in the engine, not in the adapters.** Repeat counts, choice indices, group sizes and length fields that exist only in the schema (no model member) are derived by the engine. The model is not required to supply them.
- **Processors are stateless.** They get a context per call; private state lives in `SessionState` (per binding, optionally per repeat instance). Processors never reference each other.
- **Processors communicate through published values.** A processor publishes a value (`pubns`/name, or a field's value by path); other nodes read it with `pub:`/`ref:`. A reference to a value that is not yet known is a runtime error.
- **Truncated input is an error condition, not a mode.** The reader returns `NeedMoreData`; the caller supplies the complete message and reads again from the start. The writer returns `NeedMoreSpace`/failure results the same way. Nothing is streamed/resumed across calls.
- **Results vs. exceptions.** Expected outcomes (`Success`, `NeedMoreData`, `EndOfData`, `Failure`) are result values. Schema/model/property mistakes found at runtime throw (via the engine logger's `Fail`).
- **Fixed vs. open width.** A fixed-width field consumes exactly its width. An open-width field (e.g. varint) receives a growing encoded window within one field operation; the engine does not rewind physical input through layout stages.
- **Count-less repeats** run until the end of the data; see [Open repeats](#open-repeats).
- **Stream processors run on the root only** (whole message: framing, compression, encryption).
- **Layout processors see bytes and positions** (`RootPosition`, `GroupPosition`); the engine measures widths itself. Layout declarations are outer-to-inner: the root position is the layout-input position, while group position is relative to the byte view at the processor's scope.

## Structures

- **Group**: a sequence of members. Optional `byteSize` (constant or reference) bounds its bytes: the reader limits a window, the writer buffers the group and writes the size once it is known.
- **Buffered layouts**: the engine retains independent input windows and committed positions for layout stages. Empty output skips enclosing writes; cached input can complete a read without invoking enclosing readers. NeedMoreData, optionally specifying a minimum input count, drives the same refill scheduler for fields and begin/end hooks. Only committed consumption advances a stage; unused parent output remains buffered. Every read is bounded by enclosing size windows and reserved layout tails. Fields are published/delivered individually, so a later truncated field does not roll back previously delivered fields. A range can select part of such a group and encodes those selected fields as a fresh scoped message; it is not a bit-offset slice of the original wire message.
- **Repeat**: a group with a count (constant, `ref:`, `pub:`) or *until end* (open). Each item is entered/exited as a separate frame with its own index.
- **Choice**: a group where one member is selected by an index (constant or referenced); only the selected member is visited.

## Open repeats

A repeat without a `count` (`RepeatInfo.UntilEnd`) is open: it repeats until the end of the data, or of its `byteSize` window. It is logged with a count of `-1`.

### Value model decides

A scope that holds an open repeat (its parent scope, or the root for a root repeat) can implement `IOpenRepeatSink` (read) / `IOpenRepeatSource` (write). The engine then calls `NextItem` before each item, including the first, and the model answers:

- `Next`: enter another item. On read, missing input gives the normal `NeedMoreData`.
- `End`: end the repeat; the session continues with the next sibling.
- `Default`: apply the engine rules below.

On read, `OpenRepeatReadContext` gives the next item's `Index`, the `Remaining` physical bytes up to the readable end, `AtDefaultEnd` (what `Default` would do) and `TryPeek` for the next bytes without consuming them. `TryPeek` returns false when a layout processor holds buffered input for the repeat. The model has received the previous items' values, so it can stop after a terminating item or, by peeking, before a terminator.

On write, a scope that implements `IOpenRepeatSource` is not asked `GetCount` for an open repeat; `Default` falls back to the `EndOfData` probe.

Without these interfaces the rules below apply; a repeat that the engine cannot end by itself then fails to read.

### Plan building

- **Anywhere in the schema.** An open repeat may be followed by other nodes. Plan building accepts it; the reader checks at runtime who ends it (see `RepeatInfo.EndsAtInputEnd`).
- **Engine can end it** when the repeat has a `byteSize`, or nothing follows it up to the end of the input or of an enclosing size window: it is the last member of its group (or a choice alternative), and so is every enclosing group up to the root or a group with a `byteSize`. The body of an open choice is a separate plan: its root counts as last only when the open choice itself is last (checked the same way, through nested open choices).
- **Model must end it** in every other case: the scope that holds it must implement `IOpenRepeatSink` and answer `Next` or `End`. Without the interface, or on a `Default` answer, the read fails naming the repeat path. Writing has no such restriction.
- **Bytes without a length** still must be last: a bytes field without a `byteLength` (or a group or choice that ends in one) takes the rest of the size window, so plan building reports an error when it is followed by other members. This does not apply to the members of a choice (they are alternatives, not a sequence).

### Read

Before each next item the reader decides whether the repeat ends (the engine rule):

- When a layout processor on the repeat still holds unread input (`HasBufferedInput`, or remaining layout input), it never ends.
- Inside a size window, or when the members use layout processors, it ends when the position reaches the readable end (the innermost window end, minus reserved layout tails).
- Otherwise it ends when the remaining bytes (minus reserved layout tails) are fewer than the minimum encoded size of one item (at least 1). That minimum is a lower bound derived from the plan and cached per repeat; fields with field processors, choices and nested count-less repeats count as 0.
- Trailing padding bits are read as another item. Specify the count where the end of the data is not the end of the items.

### Write

- A flat field source (`IFieldSource`) always gives an open repeat.
- A value source (`IValueSource`) is asked `GetCount`. A count of 0 or more makes it a counted repeat; `-1` (unknown) keeps it open and also skips the check of the model count against a schema count.
- Before each next item the engine probes `GetField` on the item's first field (on the scope that holds the repeat, with the next item's instance) and ends the repeat when the model answers `EndOfData`. The probe follows nested plain groups to that field; when the first member is a choice or a nested repeat the probe cannot end the repeat.
- `EndOfData` is only allowed on that probed first field; anywhere else the write fails.
- This only works for flat models; a hierarchical model must return the real count.
- The probe runs before the item is entered, so layout processors see no begin hook for an item that does not exist.

## Forward references

A value that depends on later output (e.g. a size or length written before its content) is handled by buffering the group and writing the dependent field afterwards. A seekable back-patch is a planned optimization.

## Ranges

A `PlanRange` (from path/instance to path/instance) limits a read or write to part of the plan; the cursor skips everything outside it.

## Logging

If an `ILoggerFactory` is in the services, the engine logs under `Jacobi.BinarySerializer.Engine` and each processor under its key. Without it logging is a no-op.

---

See also: [Schema](../Schema/readme.md), [Processor](../Processor/readme.md), [Pusblic API](../readme.md).
