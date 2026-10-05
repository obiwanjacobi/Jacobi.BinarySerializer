---
name: add-processor
description: Add a new built-in processor (value/semantic, field/representation, layout or stream) to Jacobi.BinarySerializer.Processors, including codec, registration, schema properties and tests. Use when asked to create a new `sys` processor.
---

# Add a processor

Processors are stateless, reusable instances. No processor-to-processor references. Private state goes in `SessionState`.

## Steps
1. Pick the kind: value/semantic (`IValueReader<,>` / `IValueWriter<,>`), field/representation (`IFieldReader<,>` / `IFieldWriter<,>`), layout (`ILayoutReader<>` / `ILayoutWriter<>`), or stream (`IStreamReader<>` / `IStreamWriter<>`).
2. If the algorithm is non-trivial and has a potential for reuse, add a static codec class in `Jacobi.BinarySerializer/Codecs/` (one class per algorithm, e.g. `Leb128Codec`) and unit test it directly.
3. Add the processor in `Jacobi.BinarySerializer.Processors/`. Look at the existing `sys:scale` (value) or `sys:varint` (field) or `sys:align` (layout) processor and copy its structure.
   - Read options from schema properties (short name; expanded to `ns.id.name` by `SchemaSet.Compile`).
   - Return `FieldReadResult<T>` / `FieldWriteResult<T>` / `LayoutReadResult<T>` (status, value, bits) or the ReadResult/WriteResult for value/semantic and stream processors.
4. Register it where the other built-in processors are registered (search for `sys:varint` or the varint processor's type name).
5. If simple to implement, add the follower interfaces. See `sys:align` for a layout processor with a stream follower. The field processor works similarly. The engine owns the buffers between stages.
6. Add tests in the Tests project: round trip (write then read), truncated input (`NeedMoreData`), and bad property values. Rare edge cases go in `EngineEdgeCaseTests`.
7. Update the TODO/done list in `Jacobi.BinarySerializer/readme.md` and the `Processor/readme.md` if behavior is documented there.

## Verify
- `run_build` on the Processors project, then `run_tests` for the new test class only; full suite once at the end.
