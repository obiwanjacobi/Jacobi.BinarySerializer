# Processors

The built-in processors of namespace `sys`. Property names are short names as used inside a processor entry in a schema. See the [Processor readme](../Jacobi.BinarySerializer/Processor/readme.md) for how processors work.

| Key | Stage | Applies to | Purpose |
|-----|-------|------------|---------|
| `sys.scale` | Semantic | field, repeat/choice (`valueProcessors`) | Scales a raw integer to a decimal. |
| `sys.enum` | Semantic | field, repeat/choice (`valueProcessors`) | Maps enum names to integers. |
| `sys.map` | Semantic | field, repeat/choice (`valueProcessors`) | Maps logical values to physical values. |
| `sys.nullable` | Semantic | field | Not implemented yet. |
| `sys.bits` | Representation | field | Extracts a range of bits from a fixed-size integer field. |
| `sys.varint` | Representation | field | Variable-length integers. |
| `sys.string` | Representation | field | Strings with a fixed length or a terminator. |
| `sys.bytepacker` | Layout | group (fields inherit) | Whole bytes in a byte order. |
| `sys.bitpacker` | Layout | group (fields inherit; `bits` on a field) | Fields of arbitrary bit widths. |
| `sys.align` | Layout | group (fields inherit) | Zero padding to a byte multiple. |
| `sys.crc` | Layout | group (fields inherit) | Appends/validates a CRC over a group. |

## Semantic (value) processors

### `sys.scale`

Converts between a raw integer and a scaled logical value: `logical = raw / scale`. Writing multiplies by `scale` and rounds (away from zero) to a `long`; reading divides and yields a `decimal`. A `null` value passes through.

| Property | Required | Description |
|----------|----------|-------------|
| `scale` | yes | Non-zero decimal factor. |

### `sys.enum`

Maps enumeration names to integers. Each property is one option: the name is the property name, the integer is its value. Writing converts a name to its integer; reading converts an integer back to its name. An unknown name or integer is an error. There are no fixed properties.

### `sys.map`

Maps logical values to physical values of the field's data type. Each property maps a logical value (name) to a physical value (value), both parsed with their data types. Reading finds the entry by physical value; writing finds it by logical value. A property with a `null` value is the default: on read it catches unmapped physical values, but it cannot be written. Also usable as a `valueProcessor` to convert a referenced value to a repeat count or choice index. At least one mapping is required.

| Property | Required | Description |
|----------|----------|-------------|
(default `sys.string`; any registered data type)

### `sys.nullable`

Placeholder: reading and writing throw `NotImplementedException`.

## Representation (field) processors

### `sys.varint`

Variable-length integers.

| Property | Required | Description |
|----------|----------|-------------|
| `encoding` | no | `leb128` (unsigned, default), `sleb128` (signed), `zigzag` (protobuf sint), `vlq` (MIDI, big-endian base-128, unsigned) or `prefix` (UTF-8 style length prefix, unsigned). |

The width is decided by the value, so the reader offers a window and the engine gives back unconsumed bytes.

### `sys.string`

Strings, delimited by exactly one of `byteLength` or `terminator`.

| Property | Required | Description |
|----------|----------|-------------|
| `encoding` | no | Text encoding name (default `utf-8`). |
| `byteLength` | one of | Fixed length in bytes. Shorter strings are padded with `padding`; trailing padding is trimmed on read. |
| `terminator` | one of | Byte value (0-255) that ends the string; not part of the string. |
| `padding` | no | Byte value (0-255) used for padding a fixed length (default 0). |

## Layout processors

### `sys.bytepacker`

Lays out fields as whole bytes in declaration order, in the configured byte order. Values without a fixed width (strings) are passed on unchanged. Usually the head of a layout chain.

| Property | Required | Description |
|----------|----------|-------------|
| `endian` | no | Group property: `little` (default) or `big` byte order of fixed-width values. |

### `sys.bitpacker`

Packs fields of arbitrary bit widths into bytes, in declaration order. A partially filled byte is padded with zero bits at the end of the group.

| Property | Required | Description |
|----------|----------|-------------|
| `bits` | no | Field property: number of bits the field occupies (default: the encoded width). |
| `bitorder` | no | Group property: `little` (default) or `big` bit order within a byte. |

### `sys.align`

Pads with zero bytes so each value, and the end of the group, starts on a multiple of `bytes`. Usable as the layout head or chained after a head such as the byte packer. Chained after a bit packer, only the bytes the packer emits are aligned; on read it cannot tell which fields consumed bits only, so combine with care.

| Property | Required | Description |
|----------|----------|-------------|
| `bytes` | yes | Alignment in bytes (> 0). |
| `relative` | no | `group` (default) or `root`: what the position is relative to. |

### `sys.crc`

Appends a CRC over all bytes of the group when writing and validates it when reading. No field represents the CRC. Usable as the layout head or chained after a head (e.g. the byte packer).

| Property | Required | Description |
|----------|----------|-------------|
| `algorithm` | no | A `CrcCodec` preset name (default `crc32`). |
| `byteorder` | no | Byte order of the CRC: `big` (default) or `little`. |
| `width` | no | Override: number of bits of the CRC (1-64), e.g. 32 for CRC-32. Also sets how many bytes are stored. |
| `poly` | no | Override: the generator polynomial in normal (non-reflected) notation, without the implicit top bit, e.g. `0x04C11DB7` for CRC-32. |
| `init` | no | Override: the initial value of the CRC register before the first byte is processed. |
| `refin` | no | Override: `true` processes each input byte least-significant bit first (reflected input). |
| `refout` | no | Override: `true` bit-reverses the register before the final xor (reflected output). |
| `xorout` | no | Override: value xor-ed into the register as the last step to get the CRC. |
| `value` | read-only | The calculated CRC value (`ulong`). |

The parameters follow the Rocksoft model, as used by the reveng CRC catalog. `algorithm` selects a preset that sets all of them; the override properties replace single parameters of that preset (or of `crc32`, when no `algorithm` is given). Numbers may be hex (`0x`).

The calculated CRC is published as `value` (`ulong`, read-only) under the namespace `sys.crc`, or under the `pubns` of the processor entry.

## Stream processors

None yet.

