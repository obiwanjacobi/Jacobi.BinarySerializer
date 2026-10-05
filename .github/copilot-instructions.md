# Copilot Instructions

## Project Guidelines
- User prefers processors to remain stateless and reusable singleton-like instances, avoiding processor-to-processor references (chain-of-responsibility coupling).
- Execution plan nodes should collect processors as a flat, ordered list (merged from schema sources); the ProcessorPipeline (its ctor) is responsible for sorting processors into stages. Each field may get its own pipeline configuration, reusing the stages (layout/stream) driven by its parent group's pipeline.

- Two public APIs are planned: one over a generic value model (values with structure) and one over typed objects (types as structure, populated instances). Only the generic one is built first. The session/engine must talk to values only through `IValueSource` (write) and `IValueSink` (read), never to a concrete model, so the typed API can be added later without refactoring the engine.
- Keep these rules for the engine: scopes live on the session's frame stack (resumable, no call-stack state); nodes are identified by plan `NodeInfo` (Name/Path); schema fields without a model member (lengths, counts, discriminators) are derived by the engine, not required from the model; derived values (repeat count, choice index) are resolved in the engine, not in the adapters.

## Code Style
- In this codebase targeting .NET 11 / C# 15, `closed` and `union` are intentionally used as new C# 15 keywords in type declarations.
- Don't call static methods on compiler aliases: e.g. use `String.IsNullOrEmpty()` instead of `string.IsNullOrEmpty()`.