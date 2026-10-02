# Copilot Instructions

## Project Guidelines
- User prefers processors to remain stateless and reusable singleton-like instances, avoiding processor-to-processor references (chain-of-responsibility coupling).
- Execution plan nodes should collect processors as a flat, ordered list (merged from schema sources); the ProcessorPipeline (its ctor) is responsible for sorting processors into stages. Each field may get its own pipeline configuration, reusing the stages (layout/stream) driven by its parent group's pipeline.

## Code Style
- In this codebase targeting .NET 11 / C# 15, `closed` is intentionally used as a new C# 15 keyword in type declarations.