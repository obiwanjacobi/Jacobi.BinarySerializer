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

## Name resolution

Data types, nodeDefs and processors are referenced by name, and `SchemaSet.Compile()` resolves all three with the same rule:

- A **local name** has no namespace (`Celsius`) or the name of its own document as namespace (`Doc.Celsius` in the document `Doc`). It refers to a def in that document only: no `sys` fallback, no search of included documents and no registry fallback. A local name without a def is a compile error ("… is not defined in the schema 'Doc'"), except for the processor case below.
- A **qualified name** (`Other.Celsius`) refers to a def in the loaded document `Other`.
- Names are case-insensitive.

What happens when no def has that name:

| Reference | Local name | Qualified name |
|-----------|------------|----------------|
| Data type (field `datatype`, `basedOn`, `valueType`) | Compile error (for `valueType`: plan-build error). | A registered data type (`sys.int32`, a custom `my.point`); an unregistered type is a plan-build error. |
| NodeDef (`nodeDef`, `memberNodeDef`) | Compile error. | `nodeDef`: compile error (a document that is not loaded is a missing schema dependency). `memberNodeDef`: not resolved at compile, see [Open choice](#open-choice). |
| Processor (`name` on a processor entry) | Without a namespace: compile error. With the own document name: a processor key (`roland.checksum` in the document `Roland`). | A processor key (`sys.varint`); an unknown key is a plan-build error. |

So built-in data types and processors always need their namespace (`sys.int32`, `sys.varint`). A def wins over a processor key with the same name, so do not give a document the name of a processor namespace (e.g. `sys`) and a def the id of one of its processors.

Inside a def, local names keep referring to the document that declares the def, also when another document uses it.

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

A node uses a definition with `nodeDef`: `sysex16` in the same document, `Midi.sysex16` from another one (see [Name resolution](#name-resolution)).

Compilation requires matching kinds. Explicit use-site scalars override the template;
omitted values inherit, including physical sizes, field values/offsets, repeat counts and choice
settings. Zero is an explicit value. An omitted choice pipeline inherits, while explicitly
setting `integrate` overrides a template's `isolate`. Defaults are applied after merging.
Properties and processors retain their existing override rules; template members precede local
members, and a local member with the name of a template member replaces it in its place (the use
wins; names compare case-insensitive). Each use receives independent inherited nodes. Local definition/type/processor references
retain the declaring document's namespace. Chained definitions are supported; cycles, missing
definitions, duplicate definition names and mismatched kinds are errors.

Sibling members (the roots of a document, the members of a group, repeat or choice, also in a
nodeDef) must have unique names (case-insensitive); a duplicate is a compile error. The members a
use lists itself are checked before they are merged with the template.

This applies to every node that references a nodeDef, roots included: a root gets the members of
its nodeDef first, then its own, and its processors and properties merge the same way. Whether a
nodeDef is the contract of an open choice is decided by the open choice (its `memberNodeDef`), not
by the nodeDef or the nodes that reference it: a root that references a nodeDef conforms to every
open choice with that nodeDef as its `memberNodeDef`, with the merged members. Open-choice contracts
must be plain group definitions. The reader/writer engine consumes the resulting effective nodes unchanged.

## Nodes

Every node has a `name`, a `kind` and optional `properties`.

| Kind | Meaning | Notable members |
|------|---------|-----------------|
| `field` | A single value. | `type`, `value`, `byteLength`, `byteOffset`, `processors` |
| `group` | An ordered sequence of members. | `members`, `byteSize` (physical size in bytes of the encoded content), `processors` |
| `repeat` | A group that repeats. | `count` (constant or reference); no count means until the end of the data. `valueProcessors` convert a referenced count. |
| `choice` | A group where exactly one member is used. | `selectedIndex` (constant or reference; without it the value model selects: an [open choice](#open-choice)), `valueProcessors` |

Nodes are addressed by path (`Png.Chunk.Body.Type`).

## Fixed schema properties

The members below are part of the schema model itself (as opposed to the free-form `properties` of a processor). The engine interprets them; processors can only read the resolved result. Members marked *constant or reference* accept a constant, a `ref:` or a `pub:` (see below).

### Fields

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `type` | The data type of the *logical* value (`UInt16`, `String`, `Bytes`, ...). It says how the value is represented in the model, not how many bytes it takes on the wire. | Choose the model type. Representation processors (varint, bit slicer) may encode it differently from the type's default fixed width. |
| `value` | An expected logical value (constant or reference): a literal of the field's value type, compared with the logical value after the semantic processors. | Check magic numbers/signatures on read (a mismatch fails). The writer supplies the value when the model has none (so the model need not know it). |
| `byteLength` | The physical length of the encoded field in bytes (constant or reference). Allowed on every data type except `Boolean`. | Fix the width of `Bytes`/`String`; give a field processor its byte count (`sys.bitslicer`); point at a length field (`ref:`) so a length prefix is read before the data and derived on write. Without a field processor, a fixed-size type needs a length equal to its size. |
| `byteOffset` | A signed offset in bytes from the current position. The field is read there and the position is restored afterwards: it is *virtual*. The model sees the value on read and must supply it on write; no bytes are written. Its value can be referenced. | Peek at bytes without consuming them: e.g. a MIDI status byte used as a choice `selectedIndex`, before the alternative reads it again. |
| `processors` | The processors (semantic, field, layout) of this field. | Transform or encode the value, see [Processors](../../Jacobi.BinarySerializer.Processors/readme.md). |

### Groups, repeats and choices

| Member | Applies to | Meaning | Use it to |
|--------|-----------|---------|-----------|
| `members` | group, repeat, choice | The ordered child nodes. | Structure. A repeat repeats all its members as one item; a choice has one alternative per member. |
| `byteSize` | group, repeat, choice | The physical size in bytes of the encoded content (constant or reference). The reader limits the content to that window; the writer derives the value from the encoded content. | Length-prefixed blocks (PNG chunk data, MIDI track). A repeat with a `byteSize` and no `count` repeats until the window is full. |
| `count` | repeat | The number of items (constant or reference). No count: repeat until the end of the data (or of the `byteSize` window). A count-less repeat followed by other nodes is ended by the value model (`IOpenRepeatSink`) on read. On write a count field the value model has no value for is derived from the items written. | Item counts stored in the data, fixed-size arrays, until-the-end lists, or terminated lists. |
| `selectedIndex` | choice | The zero-based index of the used alternative (constant or reference). Without it the choice is [open](#open-choice): the value model selects the alternative. | Pick a variant from a discriminator (type byte, tag). |
| `valueProcessors` | repeat, choice | Semantic processors that convert the referenced value to the count/index (read direction). | Convert a discriminator that is not a number (`sys.map` of a FourCC string, `sys.bits` of a status byte) to the index. |
| `processors` | group, repeat, choice | Layout processors; child nodes inherit them unless they specify their own for that stage. | Alignment, byte order, CRC over a whole group. |

#### Open choice

A choice without a `selectedIndex` is *open*: the value model selects the alternative, not the schema. Its alternatives are either:
- its `members`: the value model selects one of them by name (e.g. one entry of several of the same structure, where nothing in the data says which), or
- the groups that conform to its `memberNodeDef` (instead of `members`): an envelope with a body slot, whose body is a schema root. The nodeDef is the contract.

Rules for both:
- The value model must implement `IOpenChoiceSink` (read) / `IOpenChoiceSource` (write); read and write work the same. `EnterOpenChoice` gets an `OpenChoiceContext` (its `MemberNodeDef` is null for a choice with members) and returns the selection, made with a factory method of `OpenChoiceSinkResult` / `OpenChoiceSourceResult`:
  - `Select(scope, member)`: the whole member;
  - `SelectNode(scope, member, target, instance)`: one node inside a member that is a group (see targets below);
  - `SelectRange(scope, member, begin, beginInstance, end, endInstance)`: the nodes from `begin` up to `end` inside a member that is a group;
  - `WithScope(scope)`: the same selection with another scope (e.g. a wrapper).
- `Member` names the selected member. `Scope` is the scope of the choice, as `EnterChoice` returns it for a choice with a `selectedIndex`: the engine handles the member through it with the usual calls (`EnterGroup` for a group or a body, `SetField`/`GetField` for a field, `EnterRepeat`, `EnterChoice` or `EnterOpenChoice`) and calls `ExitChoice` on it when the choice is done (the index of the member; -1 for a body). An open choice differs from a choice with a `selectedIndex` only in the call that enters it.
- Without the interface the engine fails, naming the choice path. An unknown member throws a `SerializationException` (category `schema`) naming the choice path.
- `valueProcessors` without a `selectedIndex` is a compile error: there is no index to convert.
- An open choice cannot be the root of a schema. Open choices can be nested.

Rules for a choice with members:
- A member is selected by its name (not qualified with the document); the error for an unknown name lists the members.
- The members are part of the plan of the choice (paths like `Root.Sel.melodic.Items`), so a range of the plan can begin or end inside them like anywhere else.
- Processors and `byteSize` of the choice apply as for a choice with a `selectedIndex`.

Rules for a choice with a `memberNodeDef`:
- `memberNodeDef` names a nodeDef of kind `group`: the contract. The envelope only uses its name; the bodies that declare it depend on it. So:
  - A local name (see [Name resolution](#name-resolution)) is checked at compile: the compiler rejects a missing nodeDef or one of another kind. This is the usual setup: the contract lives in the envelope's document and the body documents refer to it.
  - A name qualified with another document is not resolved at compile and does not make that document a dependency, so a generic envelope compiles without it. A body that declares the contract does depend on its document. When no loaded root conforms (e.g. a typo), entering the open choice fails with "no group in the plan conforms".
- The `memberNodeDef` is a name, not a reference: checking it does not resolve the contract. A nodeDef that holds (also through a nested nodeDef) an open choice on itself as contract is a compile error: every body would hold the choice again. On a concrete node (a root that references the contract and lists the open choice itself) it is allowed.
- It has no `members`, `selectedIndex` or `valueProcessors`. Setting any of them (directly or through a choice nodeDef) is a compile error.
- `byteSize` works as on any group: the reader limits the selected body to that window (the body must fill it), the writer checks the encoded body against it.
- `pipeline` (only for a choice with a `memberNodeDef`) is `integrate` (default) or `isolate`. With `integrate` the selected body is built under the pipeline of the choice, so it inherits the processors of the choice and its ancestors like a nested group (layout scopes of the ancestors stay open around the body, and the body's fields read through the inherited layout chain). With `integrate` the `processors` of the open choice itself also apply to the body (e.g. a `sys.align` on the choice aligns the body's fields); the choice opens no layout scope of its own, so end-of-group padding is not added after the body. With `isolate` the body has its own pipeline: nothing of the envelope applies, and `processors` on the open choice are a compile error. Setting `pipeline="isolate"` on any other choice is a compile error. The JSON/XML property is omitted when it is `integrate`.
- A body is a schema root (a plain group) that declares `"nodeDef": "<name>"`; it has the members of the contract first, then its own (see [Reusable nodes](#reusable-nodes)). Repeat and choice roots are rejected.
- The member name is matched by full name (`document.name`), or by short name when exactly one conforming root has that name. An unknown or non-conforming group throws a `SerializationException` (category `schema`) naming the choice path and the nodeDef.
- The engine builds the plans of all conforming roots when the envelope plan is created (also for nested open choices).
- A read or write with a range that includes the open choice enters it; a range that stops before it (or does not select it) legally skips it. An included open choice must have at least one conforming body in the plan, otherwise the engine throws, naming the choice path and the nodeDef.
- A range applies to the envelope plan only: a body is a separate plan. A selected body is read or written in full, unless the value model selects a target inside it.

Targets inside the selected member (a group, or a body):
- `begin` and optionally `end` are each the path of a node (group or field) starting with the name of the member (e.g. `BodyA.Part.Sub`, `melodic.Items`). Only the fields from the first field of `begin` up to the last field of `end` (and the groups leading to them, so their layout and inherited properties apply) are visited. `SelectNode` has no `end`: only that node is visited.
- A target inside repeats is narrowed to items with `beginInstance` and `endInstance`: the item indices of the repeats around the target inside the member (outermost first, e.g. `[5]` for `BodyA.Items.Part`). Missing indices mean the first item (begin) or the last item (end); for `SelectNode` the instance applies to both ends. A range may run from a later field of an earlier item to an earlier field of a later item (e.g. `BodyA.Items.Part.P2[0]` to `BodyA.Items.Part.P1[1]`). For a member of a choice inside repeats, the items of those repeats are the current ones.
- A target in a member that is a field, a target that is not a node inside the selected member, or an `end` before `begin` throws, naming the choice path (and the target).
- A target in a member of a choice with members is only allowed when the range of the read or write covers the whole choice (it does not begin or end inside it); otherwise it throws, naming the choice path. Without a target, such a range simply applies to the member.

Querying the relation between open choices and their bodies:
- `SchemaSet` (what the schemas declare): `FindConformingGroups(memberNodeDef | openChoice)` returns the plain root groups that declare the contract; `FindOpenChoices(memberNodeDef | body)` returns the open choices (at any depth in the roots) whose contract it is. A contract name must be qualified (`document.nodeDef`); a local, unqualified `memberNodeDef` is qualified with its own document before matching.
- `ExecutionPlan` (what the plan resolves, nested open choices included): `OpenChoices` lists all open choices with a `memberNodeDef`, `GetOpenChoiceBodies(choice)` returns the built bodies by full name, `FindOpenChoices(bodyName)` returns the open choices that can select a body (full name, or a unique short name, matched like the engine does). A body selected with `integrate` and `isolate` is built twice, so its nested open choices appear once per build. Plans not created from a `SchemaSet` return empty results.

Limitations (of a choice with a `memberNodeDef`):
- Body paths are relative to the body root (e.g. `BodyA.Id`).
- Referenced groups cannot (yet) influence the pipeline mode; only the open choice decides.
- Only plans created from a `SchemaSet` have bodies; plans created from a single group or document cannot resolve an open choice.
- Bodies are built when the envelope plan is created, not at schema compile time.
- A range cannot address nodes inside a body (a body is a separate plan whose keys are relative to its own root). Select a target inside the body instead (`SelectNode`, `SelectRange`).

Address map (e.g. Roland SysEx, where an address and size point into a memory map):
- The engine does not know the map. The value model that implements `IOpenChoiceSink`/`IOpenChoiceSource` holds it (or a processor it uses): it sees the header values already read or provided (e.g. `Address`, `Size`), looks up the address and returns the selected body group and, when the address and size point to nodes nested inside that body, the target range: `begin` and `end` (with `beginInstance` and `endInstance` when the nodes are inside a repeat).
- Nested map levels are nested open choices, one decision per level, each made by the value model.
- A body per map entry (or per address range) is declared in the schema as a root that conforms to the nodeDef.
- Everything happens in one read or write call, so layout and stream processors see the whole message once. Splitting a message into several client-managed segments is not supported: layout windows and stream state are not resumable between calls.

### Shared members

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `name` | The node name; part of the path used by references and by the model. | Address a node (`Png.Chunk.Body.Type`). |
| `properties` | Free-form named values for the processors that apply to the node (e.g. `byteorder` on a group is read by the layout processors). | Configure inherited processors without repeating them. |
| `nodeDef` | A reference to a reusable node of the same kind (see [Reusable nodes](#reusable-nodes) and [Name resolution](#name-resolution)). | Share a configuration between nodes. |

### On a processor entry

| Member | Meaning | Use it to |
|--------|---------|-----------|
| `processor` | The name of a definition in `processorDefs` (`name` or `document.name`) or a processor key (`sys.varint`). | Select the processor. |
| `pubns` | The namespace the processor publishes its values under (default: its key, e.g. `sys.crc`). Interpreted by the engine, not a processor property. | Keep published values apart when the same processor is used more than once, then reference them with `pub:`. |
| other keys | The processor's own properties. | Configure the processor. |

## Values and references

A number of members (`value`, `byteLength`, `byteSize`, `count`, `selectedIndex`) accept either a constant or a reference:

- constant: `"count": 3`
- `ref:` a schema node value: JSON `{ "ref": "Png.Chunk[].Length" }`, XML `<length ref="Png.Chunk[].Length" />`. An instance index selects the repeat occurrence (`[2]`); an empty `[]` means "the same occurrence as the referrer". A path that starts with `^` is relative to the referrer: each leading `^` goes up one level, so `^.Length` is a sibling of the referrer and `^.^.Header.Length` goes up two levels and then down into `Header`. A relative reference keeps the instance of the referrer: it has no instance indices, a repeat it goes up through is the item of the referrer (as `[]`), and it cannot reach into another repeat (use an absolute path with an index for that).
- `pub:` a value published by a processor: JSON `{ "pub": "ns.name" }`, XML `<length pub="ns.name" />`. No indices.

The `ref` and `pub` keys are mutually exclusive (both present is an error; neither means unset). The `ref:`/`pub:` prefixes of the in-memory form (`SchemaNodeRef`, `SchemaPubRef`) are not written in JSON/XML; the key says which kind it is. In XML a constant is an attribute (`count="3"`) and a reference is a nested element.

A reference to a value that is not available yet is an error at runtime. Plan building checks that referenced paths exist.

## Processors in a schema

- A processor is used by name: a definition in `processorDefs`, or otherwise a processor key (`sys.varint`: namespace `sys`, id `varint`). See [Name resolution](#name-resolution).
- Properties given on the use override the properties of the definition.
- Properties of a processor use or definition can be given explicitly (`"properties": [ { "name": "byteorder", "value": "big" } ]`) or inline: any key that is not mapped to the model is a property. `{ "name": "sys.bytepacker", "byteorder": "big" }` is the same as the explicit form; in XML use attributes or child elements (`<processor name="sys.bytepacker" byteorder="big" />`).
- JSON documents may contain `//` and `/* */` comments and trailing commas.
- Inside a processor entry the properties belong to that processor, so no prefix is needed. Elsewhere the full name is used (`sys.varint.encoding`). `Compile()` expands the short names to the full form.
- Properties on fields and groups themselves are generic (e.g. `byteorder` on a group is read by the layout processors that care about it).
- A property value is a string interpreted by the processor; `null` is a valid value that a processor may give meaning.
- A processor property value can be a reference: `"byteLength": "ref:^.Len"` or `"byteLength": "pub:a.length"` (the in-memory text form, with the prefix). Every such value is bound, whether or not the processor declares the property, with the same rules as a [reference](#values-and-references) on `count`/`byteLength`. The referrer is the node the processor is on, also for properties that come from a `processorDefs` entry, and instance indices and `[]` are supported. When the plan is built, the target must be a field. For a declared property, the target's value type must convert to the data type of the property (when both are known). The text is not parsed as a constant. At runtime the processor gets the published value of the current instance: the typed getters convert it to the property's data type, and the text accessors give its invariant text. The referenced value must be published before the processor runs (an earlier field, or a `pub:` from an earlier processor); otherwise it is a runtime error that names the property. A reference inside a structured value (e.g. one element of a list) is not supported.
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
  <dataTypeDef name="Celsius" basedOn="sys.int32" scale="0.01" />
</dataTypeDefs>
<field name="Temperature" datatype="Celsius" />
```

| Attribute / element | Meaning |
|---------------------|---------|
| `name` | Name of the new data type; registered as `Document.Name`. |
| `basedOn` | The data type it derives from. |
| `min`, `max` | Allowed range of the logical value (facet), inclusive: literals of the value type, which must be comparable (numbers, strings, date/time, enums). Strings compare ordinal (`"10"` < `"9"`). With `options` the option value (and `optionsDefault`) is checked. `min` may not be larger than `max`. |
| `scale`, `shift` | Logical = physical * scale (+ shift) (facet). Also used by `sys.scale` when it has no `scale`/`shift` property. `scale` may not be 0. |
| `options` | Allowed physical values (parsed with the data type) mapped to logical values (parsed with the value type) (facet); cannot be combined with `scale`/`shift`, also not with inherited ones. Invalid combinations are a plan-build error. |
| `optionsDefault` | The logical value read for a physical value that is not one of the options (without it that is an error). Cannot be written. |
| `valueType` | The data type of the value the value model sees and provides (the logical value). Default: `sys.string` with `options`, `sys.decimal` with `scale`/`shift`, otherwise the value type of `basedOn`. |
| `processors`, `properties` | Merged into every field that uses the data type (base chain first, then the field's own). |

**Physical and logical.** `basedOn` gives the physical side: the CLR type, default representation and size of the encoded value (the built-in type at the end of the `basedOn` chain). The facets and `valueType` give the logical side: the value the value model sees. The engine converts the logical value to and from the `valueType` at the boundary with the value model, so a processor may produce any convertible type. The field's `value`, and the value a reference to the field yields, are logical values. A field that declares its own semantic processors (e.g. `sys.map`) has no value type: their output is passed on as is.

Facets just work: a field whose data type has facets and no semantic processor (on the field, its data type chain or inherited from its group) gets `sys.datatype`, which applies all facets (see the [Processors readme](../../Jacobi.BinarySerializer.Processors/readme.md)). Declaring a semantic processor replaces it; a representation processor (e.g. `sys.varint`) does not. Without the `sys` processors registered, facets are a plan-build error.

Facets are fallbacks for processors that use them: a processor's own property wins.

Data type names follow [Name resolution](#name-resolution). In addition:

- A field that uses a def gets its datatype rewritten to the full name `Document.Name`.
- Circular `basedOn` chains and unknown bases are errors.

See the [Processor readme](../Processor/readme.md) for the available properties of processors and how they are interpreted.

