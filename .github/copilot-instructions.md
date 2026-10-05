# Copilot Instructions

## Project Guidelines
- User prefers processors to remain stateless and reusable singleton-like instances, avoiding processor-to-processor references (chain-of-responsibility coupling).
- Execution plan nodes should collect processors as a flat, ordered list (merged from schema sources); the ProcessorPipeline (its ctor) is responsible for sorting processors into stages. Each field may get its own pipeline configuration, reusing the stages (layout/stream) driven by its parent group's pipeline.

- Two public APIs are planned: one over a generic value model (values with structure) and one over typed objects (types as structure, populated instances). Only the generic one is built first. The session/engine must talk to values only through `IValueSource` (write) and `IValueSink` (read), never to a concrete model, so the typed API can be added later without refactoring the engine.
- Keep these rules for the engine: scopes live on the session's frame stack (resumable, no call-stack state); nodes are identified by plan `NodeInfo` (Name/Path); schema fields without a model member (lengths, counts, discriminators) are derived by the engine, not required from the model; derived values (repeat count, choice index) are resolved in the engine, not in the adapters.

## Project Map (read this instead of exploring)
- `Jacobi.BinarySerializer` - core library. Folders: `Schema/` (schema model, JSON/XML mappers, `SchemaSet.Compile`), `Execution/` (`ExecutionPlan`, `PlanCursor`, `ReaderSession`, `WriterSession`, `SessionState`, `InstancePath`), `Processor/` (contexts, `ProcessorPipeline`, `ProcessorManager`, layout chains), `Codecs/` (bit/endian/varint codecs; one static class per algorithm).
- `Jacobi.BinarySerializer.Processors` - built-in `sys:` processors (e.g. `sys:varint`, `sys:align`).
- `Jacobi.BinarySerializer.Tests` - tests; edge cases live in `EngineEdgeCaseTests`.
- Flow: schema (JSON/XML) -> `SchemaSet.Compile` -> `ExecutionPlan` -> `PlanCursor` drives `ReaderSession`/`WriterSession` -> processors (representation/field, layout, stream) -> codecs.
- Design docs and the TODO/done list: `Jacobi.BinarySerializer/readme.md`, `Processor/readme.md`, `Schema/readme.md`. Read the relevant one before changing that area; update the TODO list when finishing an item. Add new items to the TODO list if you find a missing feature or edge case.

## Workflow (token efficiency)
- Use `find_symbol` / `grep_search` for targeted lookups; do not read whole large files (sessions, `SchemaSet`) - read the needed line range.
- Do not re-read the readmes or this file if already in context.
- Build only the affected project (`run_build` with projectPath), then run only the relevant tests (`run_tests` by TypeName); run the full suite once at the end.
- Don't add a plan for single-area changes; keep edits minimal and don't refactor unrelated code.
- Don't paste code blocks of changes in replies; keep answers short.
- Add tests for new engine behavior in the Tests project, next to similar existing tests.
- Editing safety: the edit tool replaces whole lines, so `oldString`/`newString` must cover complete lines (never start or end mid-line). Re-read the target lines right before editing (the user edits files too, e.g. ticking TODO items), and re-read them after editing to verify. This matters most for `readme.md` TODO lists.
- Do not add interfaces to processors to solve engine needs (e.g. a read-window interface); extend the contexts or the engine instead.
- Truncated input is an error: callers must supply a complete message. Do not design for resuming/streaming reads.

## Domain Terms
- Processor kinds: Semantic/value (value <-> value), Representation/field (value <-> encoded field), Layout (bytes in a group/field, e.g. align), Stream (whole-message: framing, compression, encryption).
- Processor chains: head processor plus same-signature followers; the plan builder validates chains (`ValidateFieldChain`, `ValidateLayoutChain`).
- Node refs: `ref:` (reads a schema node value, supports instance indices `[2]`, `[]`). `pub:` reads a published value (no indices). Processor refs: key or `ref:[doc.]alias`; short property names expand to `ns.id.name`.
- Value models: `IValueSource`/`IValueSink` (structured) and `IFieldSource`/`IFieldSink` (flat, uses `Instance` in contexts).

## Code Style
- In this codebase targeting .NET 11 / C# 15, `closed` and `union` are intentionally used as new C# 15 keywords in type declarations.
- Don't call static methods on compiler aliases: e.g. use `String.IsNullOrEmpty()` instead of `string.IsNullOrEmpty()`.
- Write doc-comments on new lines. Not ///<summary>...</summary> on the same line. Except for very short descriptions.
- Don't add interfaces to processors without user consent. Ever. The user prefers to keep the processor API minimal and stable.
