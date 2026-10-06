# Schema

A schema describes the structure of a binary format: which fields exist, in what order, how they repeat or branch and which processors transform them. It describes *structure only*. The code that does the transforming lives in processors, the walking is done by the [engine](../Execution/readme.md).

## General idea

- A **schema document** (JSON, XML, ...) is loaded into a `SchemaSet`. All formats map onto the same in-memory model (`SchemaDocument`, `SchemaNode`).
- `SchemaSet.Compile()` resolves references (between documents, to type definitions and to processor definitions), expands property names and validates. A document must be compiled before a plan can be built from it.
- A document has one or more **roots**. A root is a group that is selected by name when the serializer asks for an `ExecutionPlan`.
- The schema is data: it holds no logic. Everything that is not plain structure is expressed by a **processor** (with properties) or by a **reference**.

## Document layout

| Section | Purpose |
|---------|---------|
| `name` | Name of the document (namespace for references from other documents). |
| `includes` | Other documents this one refers to. |
| `typeDefs` | Reusable group and field types: a data type plus processors. |
| `processorDefs` | Named processor configurations (a processor key with default properties). |
| `properties` / `processors` | Document-level defaults. |
| `children` | The root groups. |

## Nodes

Every node has a `name`, a `kind` and optional `properties`.

| Kind | Meaning | Notable members |
|------|---------|-----------------|
| `field` | One value. | `type` (data type), `value` (constant or reference), `length` (for `Bytes`), `processors` |
| `group` | An ordered sequence of children. | `children`, `size`, `processors` |
| `repeat` | A group that repeats. | `count` (constant or reference); no count means until the end of the data. `valueProcessors` convert a referenced count. |
| `choice` | A group where exactly one child is used. | `selectedIndex` (constant or reference), `valueProcessors` |

Nodes are addressed by path (`Png.Chunk.Body.Type`).

## Values and references

A number of members (`value`, `length`, `size`, `count`, `selectedIndex`) accept either a constant or a reference:

- constant: `"count": 3`
- `ref:` a schema node value: `{ "reference": "ref:Png.Chunk[].Length" }`. An instance index selects the repeat occurrence (`[2]`); an empty `[]` means "the same occurrence as the referrer".
- `pub:` a value published by a processor: `{ "reference": "pub:ns.name" }`. No indices.

A reference to a value that is not available yet is an error at runtime. Plan building checks that referenced paths exist.

## Processors in a schema

- A processor is used by its key (`sys.varint`: namespace `sys`, id `varint`) or by a reference to a definition (`ref:name`, or `ref:document.name` from another document).
- Properties given on the use override the properties of the definition.
- Inside a processor entry the properties belong to that processor, so no prefix is needed. Elsewhere the full name is used (`sys.enum.map`). `Compile()` expands the short names to the full form.
- Properties on fields and groups themselves are generic (e.g. `byteorder` on a group is read by the layout processors that care about it).
- A property value is a string interpreted by the processor; `null` is a valid value that a processor may give meaning.
- Processors are sorted into stages by the pipeline. A child node inherits the stages of its parent and replaces a stage only when it specifies processors for it.

## How to...

**Define a field with a fixed type:** `{ "name": "Width", "kind": "field", "type": "UInt32" }`

**Check a magic number:** give the field a `value`. The writer supplies it when the model has none; the reader fails when the data differs.

**Read a fixed number of raw bytes:** type `Bytes` with `length`. Without a length the field takes the rest of the enclosing sized group.

**Make a count/length that precedes its data:** add a field for it and reference it: `"count": { "reference": "ref:Header.Count" }`. The writer derives the referenced field from the model; it does not have to exist in the model.

**Bound a group by a byte size:** use `size` (constant or reference). The reader limits the group to that window; the writer derives the value from the encoded content.

**Select a variant:** use a `choice` with a `selectedIndex` reference to a discriminator field. If the discriminator is not a number, add `valueProcessors` (e.g. a `sys.map` processor definition) that convert it to an index.

**Transform a value (scale, enum, nullable):** add the processor to the field's `processors` with its properties.

**Reuse a configuration:** declare it in `processorDefs` (or a type in `typeDefs`) and use `ref:name`.

**Share definitions between files:** add an `include` and reference with `ref:document.name`.

**Apply a layout to many fields (alignment, bit/byte packing):** put the layout processor on the group; the fields inherit it.

See the [Processor readme](../Processor/readme.md) for the available properties of processors and how they are interpreted.

