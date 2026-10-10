# Schema

A schema describes the structure of a binary format: which fields exist, in what order, how they repeat or branch and which processors transform them. It describes *structure only*. The code that does the transforming lives in processors, the walking is done by the [engine](../Execution/readme.md).

## General idea

- A **schema document** (JSON, XML, YML) is loaded into a `SchemaSet`. All formats map onto the same in-memory model (`SchemaDocument`, `SchemaNode`).
- `SchemaSet.Compile()` resolves references (between documents, to type definitions and to processor definitions), expands property names and validates. A document must be compiled before a plan can be built from it.
- A document has one or more **roots**. A root is a group that is selected by name when the serializer asks for an `ExecutionPlan`.
- The schema is data: it holds no logic. Everything that is not plain structure is expressed by a **processor** (with properties) or by a **reference**.

## Document layout

| Section | Purpose |
|---------|---------|
| `name` | Name of the document (namespace for references from other documents). |
| `includes` | Other documents this one refers to. |
| `nodeDefs` | Reusable group and field types: an optional data type (fields only) plus processors. |
| `dataTypeDefs` | New data types based on an existing one: facets (`min`, `max`, `scale`, `shift`, `options`) plus processors. |
| `processorDefs` | Named processor configurations (a processor key with default properties). |
| `properties` / `processors` | Document-level defaults. |
| `members` | The root groups. |

## Nodes

Every node has a `name`, a `kind` and optional `properties`.

| Kind | Meaning | Notable members |
|------|---------|-----------------|
| `field` | A single value. | `type`, `value`, `byteLength`, `byteOffset`, `processors` |
| `group` | An ordered sequence of members. | `members`, `byteSize` (physical size in bytes of the encoded content), `processors` |
| `repeat` | A group that repeats. | `count` (constant or reference); no count means until the end of the data. `valueProcessors` convert a referenced count. |
| `choice` | A group where exactly one member is used. | `selectedIndex` (constant or reference), `valueProcessors` |

Nodes are addressed by path (`Png.Chunk.Body.Type`).

## Fixed schema properties

The members below are part of the schema model itself (as opposed to the free-form `properties` of a processor). The engine interprets them; processors can only read the resolved result. Members marked *constant or reference* accept a constant, a `ref:` or a `pub:` (see below).

### Fields

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `type` | The data type of the *logical* value (`UInt16`, `String`, `Bytes`, ...). It says how the value is represented in the model, not how many bytes it takes on the wire. | Choose the model type. Representation processors (varint, bit slicer) may encode it differently from the type's default fixed width. |
| `value` | An expected value (constant or reference), compared with the logical value after the semantic processors. | Check magic numbers/signatures on read (a mismatch fails). The writer supplies the value when the model has none (so the model need not know it). |
| `byteLength` | The physical length of the encoded field in bytes (constant or reference). Allowed on every data type except `Boolean`. | Fix the width of `Bytes`/`String`; give a field processor its byte count (`sys.bitslicer`); point at a length field (`ref:`) so a length prefix is read before the data and derived on write. Without a field processor, a fixed-size type needs a length equal to its size. |
| `byteOffset` | A signed offset in bytes from the current position. The field is read there and the position is restored afterwards: it is *virtual*. The model sees the value on read and must supply it on write; no bytes are written. Its value can be referenced. | Peek at bytes without consuming them: e.g. a MIDI status byte used as a choice `selectedIndex`, before the alternative reads it again. |
| `processors` | The processors (semantic, field, layout) of this field. | Transform or encode the value, see [Processors](../../Jacobi.BinarySerializer.Processors/readme.md). |

### Groups, repeats and choices

| Member | Applies to | Meaning | Use it to |
|--------|-----------|---------|-----------|
| `members` | group, repeat, choice | The ordered child nodes. | Structure. A repeat repeats all its members as one item; a choice has one alternative per member. |
| `byteSize` | group, repeat, choice | The physical size in bytes of the encoded content (constant or reference). The reader limits the content to that window; the writer derives the value from the encoded content. | Length-prefixed blocks (PNG chunk data, MIDI track). A repeat with a `byteSize` and no `count` repeats until the window is full. |
| `count` | repeat | The number of items (constant or reference). No count: repeat until the end of the data (or of the `byteSize` window). | Item counts stored in the data, fixed-size arrays, or until-the-end lists. |
| `selectedIndex` | choice | The zero-based index of the used alternative (constant or reference). | Pick a variant from a discriminator (type byte, tag). |
| `valueProcessors` | repeat, choice | Semantic processors that convert the referenced value to the count/index (read direction). | Convert a discriminator that is not a number (`sys.map` of a FourCC string, `sys.bits` of a status byte) to the index. |
| `processors` | group, repeat, choice | Layout/stream processors; child nodes inherit them unless they specify their own for that stage. | Bit/byte packing, alignment, CRC over a whole group. |

### Shared members

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `name` | The node name; part of the path used by references and by the model. | Address a node (`Png.Chunk.Body.Type`). |
| `properties` | Free-form named values for the processors that apply to the node (e.g. `byteorder` on a group is read by the layout processors). | Configure inherited processors without repeating them. |
| `nodeDef` | A reference to a reusable type (data type plus processors). | Share a configuration between nodes. |

### On a processor entry

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `processor` | The processor key (`sys.varint`) or a `ref:` to a definition. | Select the processor. |
| `pubns` | The namespace the processor publishes its values under (default: its key, e.g. `sys.crc`). Interpreted by the engine, not a processor property. | Keep published values apart when the same processor is used more than once, then reference them with `pub:`. |
| other keys | The processor's own properties. | Configure the processor. |

## Values and references

A number of members (`value`, `byteLength`, `byteSize`, `count`, `selectedIndex`) accept either a constant or a reference:

- constant: `"count": 3`
- `ref:` a schema node value: JSON `{ "ref": "Png.Chunk[].Length" }`, XML `<length ref="Png.Chunk[].Length" />`. An instance index selects the repeat occurrence (`[2]`); an empty `[]` means "the same occurrence as the referrer".
- `pub:` a value published by a processor: JSON `{ "pub": "ns.name" }`, XML `<length pub="ns.name" />`. No indices.

The `ref` and `pub` keys are mutually exclusive (both present is an error; neither means unset). The `ref:`/`pub:` prefixes of the in-memory form (`SchemaNodeRef`, `SchemaPubRef`) are not written in JSON/XML; the key says which kind it is. In XML a constant is an attribute (`count="3"`) and a reference is a nested element.

A reference to a value that is not available yet is an error at runtime. Plan building checks that referenced paths exist.

## Processors in a schema

- A processor is used by its key (`sys.varint`: namespace `sys`, id `varint`) or by a reference to a definition (`ref:name`, or `ref:document.name` from another document).
- Properties given on the use override the properties of the definition.
- Properties of a processor use or definition can be given explicitly (`"properties": [ { "name": "byteorder", "value": "big" } ]`) or inline: any key that is not mapped to the model is a property. `{ "name": "sys.bytepacker", "byteorder": "big" }` is the same as the explicit form; in XML use attributes or child elements (`<processor name="sys.bytepacker" byteorder="big" />`).
- JSON documents may contain `//` and `/* */` comments and trailing commas.
- Inside a processor entry the properties belong to that processor, so no prefix is needed. Elsewhere the full name is used (`sys.enum.map`). `Compile()` expands the short names to the full form.
- Properties on fields and groups themselves are generic (e.g. `byteorder` on a group is read by the layout processors that care about it).
- A property value is a string interpreted by the processor; `null` is a valid value that a processor may give meaning.
A child node inherits the stages of its parent

## How to...

**Define a field with a fixed type:** `{ "name": "Width", "kind": "field", "type": "UInt32" }`

**Check a magic number:** give the field a `value`. The writer supplies it when the model has none; the reader fails when the data differs.

**Read a fixed number of raw bytes:** type `Bytes` with `byteLength` (always a byte count, also for `String`). Without a byte length the field takes the rest of the enclosing sized group.

**Give a field a physical length:** `byteLength` is allowed on every data type except `Boolean`. The data type is the logical representation, so the length says how many bytes the encoded form takes. Without a field processor a fixed-size type must have a constant length equal to its size; with a field processor (e.g. `sys.bitslicer`, `sys.varint`) the processor reads the length from the field.

**Bound a repeat without a count:** give a `repeat` a `byteSize` and no `count`; it repeats until the end of that window (and derives the size on write).

**Peek at a value without consuming it:** a virtual field (`byteOffset`), e.g. `byteOffset=0` to read the status byte that a choice uses as `selectedIndex` before the alternative reads it.

`"count": { "ref": "Header.Count" }`.

**Bound a group by a byte size:** use `byteSize` (constant or reference). The reader limits the group to that window; the writer derives the value from the encoded content.

**Select a variant:** use a `choice` with a `selectedIndex` reference to a discriminator field. If the discriminator is not a number, add `valueProcessors` (e.g. a `sys.map` processor definition) that convert it to an index.

**Transform a value (scale, enum, nullable):** add the processor to the field's `processors` with its properties.

**Reuse a configuration:** declare it in `processorDefs` (or a type in `nodeDefs`) and use `ref:name`.

**Define a new data type:** declare it in `dataTypeDefs` with a `name`, a `basedOn` data type, optional facets (`min`, `max`, `scale`, `shift`, `options`), `processors` and `properties`, then use it as the field's `datatype` (see below).

**Share definitions between files:** add an `include` and reference with `ref:document.name`.

**Apply a layout to many fields (alignment, bit/byte packing):** put the layout processor on the group; the fields inherit it.

## Data type definitions

A `dataTypeDef` derives a new logical data type from an existing one (built-in or another def). It inherits the CLR type, parse and default encode/decode of its base.

```xml
<dataTypeDefs>
  <dataTypeDef name="Celsius" basedOn="Int32" scale="100">
    <processors><processor name="sys.scale" /></processors>
  </dataTypeDef>
</dataTypeDefs>
<field name="Temperature" datatype="Celsius" />
```

| Attribute / element | Meaning |
|---------------------|---------|
| `name` | Name of the new data type; registered as `Document.Name`. |
| `basedOn` | The data type it derives from. |
| `min`, `max` | Allowed logical range (facet). |
| `scale`, `shift` | Logical = physical / scale (+ shift) (facet). Used by `sys.scale` when it has no `scale` property. |
| `options` | Allowed physical values mapped to names (facet; not consumed by a built-in processor yet). |
| `processors`, `properties` | Merged into every field that uses the data type (base chain first, then the field's own). |

Facets are fallbacks: a processor's own property wins. Resolution rules:

- A field that uses a def gets its datatype rewritten to `Document.Name`; use `Document.Name` to refer to a def in another (included) document.
- A name without a namespace is first looked up in the document's own `dataTypeDefs` (a local def wins over a built-in of the same name), otherwise it is a built-in `sys` type.
- Circular `basedOn` chains and unknown bases are errors.

See the [Processor readme](../Processor/readme.md) for the available properties of processors and how they are interpreted.

