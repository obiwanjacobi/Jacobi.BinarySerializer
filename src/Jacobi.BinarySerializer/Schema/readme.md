# Schema

A schema describes the structure of a binary format: which fields exist, in what order, how they repeat or branch and which processors transform them. It describes *structure only*. The code that does the transforming lives in processors, the walking is done by the [engine](../Execution/readme.md).

## General idea

- A **schema document** (JSON, XML, YML) is loaded into a `SchemaSet`. All formats map onto the same in-memory model (`SchemaDocument`, `SchemaNode`).
- `SchemaSet.Compile()` resolves references (between documents, to type definitions and to processor definitions), expands property names and validates. A document must be compiled before a plan can be built from it.
- A document has one or more **roots**. A root is a group that is selected by name when the serializer asks for an `ExecutionPlan`.
- The schema is data: it holds no logic. Everything that is not plain structure is expressed by a **processor** (with properties) or by a **reference**.

## Schema construction

`SchemaDocumentBuilder` constructs a schema directly from fluent operations. It does not
import documents, manage mutable editing drafts or create deep-copy snapshots. Editing models
and their conversion to runtime schemas belong to the consuming application.
Node collections remain getter-only. Use `Members` and `NodeDefs` callbacks to populate them;
the builder maintains the document's `Roots`, `Groups` and `Fields` indexes. Node operations
accept properties, processor declarations preserve publish namespaces, and includes accept paths.
The constructor accepts document-level settings, properties and processors.

`VirtualField`, `OpenChoice`, `SizedGroup` and `RepeatToEnd` name special authoring cases.
Field values/lengths, group sizes and repeat/choice value processors are available as typed
optional arguments. Building does not replace `SchemaSet.Compile()` validation or permit incomplete drafts at runtime.

XML DTO collections are nullable: the mapper writes empty collections as null so the framework
serializer omits their containers. Missing and explicitly empty XML collections map to the same
non-null empty schema collections. No element-name-based output cleanup is required.

Status:
- [x] Direct schema construction without editor-model dependencies.
- [x] Typed special-case builder configuration.
- [x] Builder support for authored metadata while keeping node collections read-only.
- [x] JSON/XML/YAML round-trip tests.
- [x] Accept inline/wrapped XML processor declarations and omit empty generated collections/unused namespaces.
- [x] Reuse concrete nodes for definitions, with typed inheritance, independent instances and reference-cycle checks.

## Document layout

| Section | Purpose |
|---------|---------|
| `name` | Name of the document (namespace for references from other documents). |
| `includes` | Other documents this one refers to. |
| `nodeDefs` | Reusable field, group, repeat and choice nodes with all their typed settings. |
| `dataTypeDefs` | New data types based on an existing one: facets (`min`, `max`, `scale`, `shift`, `options`) plus processors. |
| `processorDefs` | Named processor configurations (a processor key with default properties). |
| `properties` / `processors` | Document-level defaults. |
| `members` | The root groups. |

## Reusable nodes

`Schema.NodeDefs` holds ordinary `SchemaNode` instances. There is no separate definition model.
JSON/YAML definitions use the same `kind` discriminator and properties as ordinary members.
The JSON schema reuses the node `oneOf`; XML reuses the node-list `xs:choice`:

```xml
<nodeDefs>
  <field name="sysex16" datatype="sys.uint16" byteLength="2">
    <processor name="midi.sysexbyte" />
  </field>
</nodeDefs>
```

Use `builder.NodeDefs(nodes => nodes.Field("sysex16", "sys.uint16", byteLength: 2))` for programmatic definitions; the callback offers the same typed operations as normal members.

Compilation requires matching kinds. Explicit use-site scalars override the template;
omitted values inherit, including physical sizes, field values/offsets, repeat counts and choice
settings. Zero is an explicit value. An omitted choice pipeline inherits, while explicitly
setting `integrate` overrides a template's `isolate`. Defaults are applied after merging.
Properties and processors retain their existing override rules; template members precede local
members. Each use receives independent inherited nodes. Local definition/type/processor references
retain the declaring document's namespace. Chained definitions are supported; cycles, missing
definitions, duplicate names and mismatched kinds are errors.

Plain-group roots that declare a nodeDef retain their open-choice-contract behavior: they list
their own members rather than importing contract members. Open-choice contracts must be plain
group definitions. The reader/writer engine consumes the resulting effective nodes unchanged.

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
| `processors` | group, repeat, choice | Layout processors; child nodes inherit them unless they specify their own for that stage. | Alignment, byte order, CRC over a whole group. |

#### Open choice

A choice with `memberNodeDef` (instead of `members`) is *open*: an envelope with a body slot whose group is selected by the value model, not by the schema. The nodeDef is the contract.

Rules:
- `memberNodeDef` names a nodeDef of kind `group` (same document, or qualified `document.name`). The compiler rejects a missing nodeDef or one of another kind.
- `selectedIndex` is optional and not used by an open choice.
- `pipeline` (open choice only) is `integrate` (default) or `isolate`. With `integrate` the selected body is built under the pipeline of the choice, so it inherits the processors of the choice and its ancestors like a nested group (layout scopes of the ancestors stay open around the body, and the body's fields read through the inherited layout chain). With `isolate` the body has its own pipeline: nothing of the envelope applies. Setting `pipeline="isolate"` on a choice that is not open is a compile error. The JSON/XML property is omitted when it is `integrate`.
- A body is a schema root (a plain group) that declares `"nodeDef": "<name>"`. Repeat and choice roots are rejected.
- The value model must implement `IOpenChoiceSink` (read) / `IOpenChoiceSource` (write) and return the scope plus the name of the selected group. Without the interface the engine fails, naming the choice path.
- The group name is matched by full name (`document.name`), or by short name when exactly one conforming root has that name. An unknown or non-conforming group throws an `InvalidOperationException` naming the choice path and the nodeDef.
- The engine builds the plans of all conforming roots when the envelope plan is created (also for nested open choices).
- An open choice cannot be the root of a schema.
- Open choice nodes can be nested.
- A read or write with a range that includes the open choice enters it; a range that stops before it (or does not select it) legally skips it. An included open choice must have at least one conforming body in the plan, otherwise the engine throws, naming the choice path and the nodeDef.
- A range applies to the envelope plan only. A selected body is read or written in full, unless the value model returns a `Target`: the path of a group inside the body, starting with the body root name (e.g. `BodyA.Part.Sub`). Then only that group (and the groups leading to it, so their layout and inherited properties apply) is visited. A target that is not a group inside the selected body throws, naming the choice path and the target. A target inside repeats can be narrowed to one item with a `TargetInstance`: the item indices of the repeats around the target inside the body (outermost first, e.g. `[5]` for `BodyA.Items.Part`). Without it all items are visited.

Limitations:
- Body paths are relative to the body root (e.g. `BodyA.Id`).
- Referenced groups cannot (yet) influence the pipeline mode; only the open choice decides.
- `byteSize` and `processors` on the open choice itself are ignored.
- Body roots are not merged with the members of their nodeDef (nested nodes are); a body must list all its own members.
- Only plans created from a `SchemaSet` have bodies; plans created from a single group or document cannot resolve an open choice.
- Bodies are built when the envelope plan is created, not at schema compile time.
- A range cannot address nodes inside a body (a body is a separate plan whose keys are relative to its own root). Use `Target` and `TargetInstance` to select a group occurrence inside the body.

Address map (e.g. Roland SysEx, where an address and size point into a memory map):
- The engine does not know the map. The value model that implements `IOpenChoiceSink`/`IOpenChoiceSource` holds it (or a processor it uses): it sees the header values already read or provided (e.g. `Address`, `Size`), looks up the address and returns the selected body group and, when the address points to a group nested inside that body, its `Target` path (with a `TargetInstance` when the group is inside a repeat).
- Nested map levels are nested open choices, one decision per level, each made by the value model.
- A body per map entry (or per address range) is declared in the schema as a root that conforms to the nodeDef.
- Everything happens in one read or write call, so layout and stream processors see the whole message once. Splitting a message into several client-managed segments is not supported: layout windows and stream state are not resumable between calls.

### Shared members

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `name` | The node name; part of the path used by references and by the model. | Address a node (`Png.Chunk.Body.Type`). |
| `properties` | Free-form named values for the processors that apply to the node (e.g. `byteorder` on a group is read by the layout processors). | Configure inherited processors without repeating them. |
| `nodeDef` | A reference to a reusable type (data type plus processors). | Share a configuration between nodes. |

### On a processor entry

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `processor` | The name of a definition in `processorDefs` (`name` or `document.name`) or a processor key (`sys.varint`). | Select the processor. |
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

- A processor is used by name, looked up like a data type name:
  - `name` (no namespace): a definition in the `processorDefs` of the same document. It must exist.
  - `document.name`: a definition in that (included) document; the own document name is allowed too.
  - any other `namespace.id`: a processor key (`sys.varint`: namespace `sys`, id `varint`).
  - A definition wins over a processor key with the same name, so do not give a document the name of a processor namespace (e.g. `sys`) and a definition the id of one of its processors. Names are case-insensitive.
- Properties given on the use override the properties of the definition.
- Properties of a processor use or definition can be given explicitly (`"properties": [ { "name": "byteorder", "value": "big" } ]`) or inline: any key that is not mapped to the model is a property. `{ "name": "sys.bytepacker", "byteorder": "big" }` is the same as the explicit form; in XML use attributes or child elements (`<processor name="sys.bytepacker" byteorder="big" />`).
- JSON documents may contain `//` and `/* */` comments and trailing commas.
- Inside a processor entry the properties belong to that processor, so no prefix is needed. Elsewhere the full name is used (`sys.varint.encoding`). `Compile()` expands the short names to the full form.
- Properties on fields and groups themselves are generic (e.g. `byteorder` on a group is read by the layout processors that care about it).
- A property value is a string interpreted by the processor; `null` is a valid value that a processor may give meaning.
A child node inherits the stages of its parent. Value, field and stream stages: processors declared on a child replace the inherited ones for that stage (the nearest declaration wins). Layout stage: processors declared on a group nest inside the layout scopes of its ancestors: each group begins/ends only the layout processors it declares itself, while a field reads/writes through the whole layout chain (all layout processors declared from the root down to the field, outermost first). The innermost layout processor handles encoded fields; each outer processor wraps it and must implement the chained layout interfaces (`ILayoutReader<ReadOnlyMemory<byte>>` / `ILayoutWriter<ReadOnlySpan<byte>>`). Reads flow outer-to-inner, writes inner-to-outer, and group hooks begin outer-to-inner and end inner-to-outer. A processor already in the chain (same key) is not added again. The plan builder reports a non-chainable outer processor with the path of the group.

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

**Reuse a configuration:** declare it in `processorDefs` (or a node in `nodeDefs`) and use it by `name`.

**Define a new data type:** declare it in `dataTypeDefs` with a `name`, a `basedOn` data type, optional facets (`min`, `max`, `scale`, `shift`, `options`), `processors` and `properties`, then use it as the field's `datatype` (see below).

**Share definitions between files:** add an `include` and reference with `document.name`.

**Apply a layout to many fields (alignment, byte order, CRC):** put the layout processor on the group; the fields inherit it.

## Data type definitions

A `dataTypeDef` derives a new logical data type from an existing one (built-in or another def). It inherits the CLR type, parse and default encode/decode of its base.

```xml
<dataTypeDefs>
  <dataTypeDef name="Celsius" basedOn="sys.int32" scale="0.01">
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
| `scale`, `shift` | Logical = physical * scale (+ shift) (facet). Used by `sys.scale` when it has no `scale` property. |
| `options` | Allowed physical values mapped to names (facet; not consumed by a built-in processor yet). |
| `processors`, `properties` | Merged into every field that uses the data type (base chain first, then the field's own). |

Facets are fallbacks: a processor's own property wins. Resolution rules:

- A field that uses a def gets its datatype rewritten to `Document.Name`; use `Document.Name` to refer to a def in another (included) document.
- Data type names are always full names (`sys.int32`, `Document.Celsius`); there is no `sys` shorthand. Names are case-insensitive.
- A name without a namespace refers to a def in the document it is used in. Built-in types always need the `sys.` prefix.
- Circular `basedOn` chains and unknown bases are errors.

See the [Processor readme](../Processor/readme.md) for the available properties of processors and how they are interpreted.

