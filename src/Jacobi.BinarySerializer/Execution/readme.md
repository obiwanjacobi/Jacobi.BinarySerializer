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
| `ExecutionPlan` | Immutable tree of `NodeInfo` (`GroupInfo`, `PackedGroupInfo`, `RepeatInfo`, `ChoiceInfo`, `FieldInfo`). Each node has a path, its resolved processor pipeline and its bound references. Plans are cached by the `Serializer`. |
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
- **Fixed vs. open width.** A fixed-width field consumes exactly its width. An open-width field (e.g. varint) is offered a window and the engine returns the bytes that were not consumed.
- **Count-less repeats on write.** A repeat without a `count` or `byteSize` is open. A flat field source (`IFieldSource`) always gives an open repeat. A value source (`IValueSource`) answers `GetCount` with `-1` (unknown) to get the same: the engine then calls `GetField` on the first field of each next item (on the same source, with the item instance) and ends the repeat when the model answers `EndOfData`. This only works for flat models; a hierarchical model must return the real count. A `-1` also skips the check of the model count against a schema count.
- **Stream processors run on the root only** (whole message: framing, compression, encryption).
- **Layout processors see bytes and positions** (`RootPosition`, `GroupPosition`); the engine measures widths itself.

## Structures

- **Group**: a sequence of members. Optional `byteSize` (constant or reference) bounds its bytes: the reader limits a window, the writer buffers the group and writes the size once it is known.
- **Packed group**: `PackedGroupInfo` retains ordered logical `Fields` as its `Members`, with normal parent/index/path metadata and value pipelines. It binds a pre-layout carrier `ByteLength` (default 1 when the schema has none) separately from group `ByteSize` and selects one group-local `PackingProcessor`. Child representation/layout declarations are rejected; layout is retained for the carrier. Child default encoded widths do not determine open-endedness or require byte windows. Length references publish their target logical fields normally; the carrier cannot depend on its own children.
  - **Cursor and ranges**: a packed group is one atomic cursor step (`CursorStepKind.PackedGroup`). A `PlanRange` must include all fields of a packed group or none of them.
  - **Write**: each child runs its value pipeline (constants, derived values, value processors); the packer is called once with the ordered logical fields and returns the carrier; the carrier then goes through the group/ancestor layout chain once.
  - **Read**: layout takes exactly `ByteLength` bytes (bounded by every enclosing size window and by tail bytes a layout processor reserved, such as a CRC); the packer is called once with the carrier and returns one logical field per child in declaration order; each child then runs its reverse value pipeline, expected-value check, publication and the sink. A partial carrier returns `NeedMoreData` and nothing is delivered to the sink.
  - **Carrier checks**: the resolved length must be positive and at most `Int32.MaxValue / 8`. The carrier must be exactly `ByteLength * 8` bits; `byte[]`, `Memory<byte>` and `ReadOnlyMemory<byte>` values are accepted and normalized to `byte[]` before layout and before the packer reads. The read result must contain the child names in order and consume exactly the carrier bits.
  - **Open repeats**: a repeat without a count that contains packed groups ends when the readable bytes (enclosing windows and layout-reserved tails, such as a CRC, excluded) are used up.
  - **Limits**: a packed child cannot use a deferred size derivation; this fails explicitly. The carrier length is never derived from the packer.
- **Repeat**: a group with a count (constant, `ref:`, `pub:`) or *until end* (open). Each item is entered/exited as a separate frame with its own index.
- **Choice**: a group where one member is selected by an index (constant or referenced); only the selected member is visited.

## Forward references

A value that depends on later output (e.g. a size or length written before its content) is handled by buffering the group and writing the dependent field afterwards. A seekable back-patch is a planned optimization.

## Ranges

A `PlanRange` (from path/instance to path/instance) limits a read or write to part of the plan; the cursor skips everything outside it.

## Logging

If an `ILoggerFactory` is in the services, the engine logs under `Jacobi.BinarySerializer.Engine` and each processor under its key. Without it logging is a no-op.

---

See also: [Schema](../Schema/readme.md), [Processor](../Processor/readme.md), [Pusblic API](../readme.md).
