# Processors

The built-in processors of namespace `sys`. Property names are short names as used inside a processor entry in a schema. See the [Processor readme](../Jacobi.BinarySerializer/Processor/readme.md) for how processors work.

| Key | Stage | Applies to | Purpose |
|-----|-------|------------|---------|
| `sys.scale` | Semantic | field, repeat/choice (`valueProcessors`) | Scales a raw integer to a decimal. |
| `sys.map` | Semantic | field, repeat/choice (`valueProcessors`) | Maps source values to result values (enumerations, discriminators). |
| `sys.nullable` | Semantic | field | Not implemented yet. |
| `sys.bits` | Semantic | field, repeat/choice (`valueProcessors`) | Extracts a range of bits from an integer value. |
| `sys.varint` | Representation | field | Variable-length integers. |
| `sys.bitslicer` | Representation | field | Joins the low bits of each byte of one field (e.g. 7 bits: MIDI 14-bit values). |
| `sys.string` | Representation | field | Strings with a fixed length or a terminator. |
| `sys.bytepacker` | Layout | group (fields inherit) | Whole bytes in a byte order. |
| `sys.bitpacker` | Packing | `packed` (`bits` on each child) | Fields of arbitrary bit widths in one carrier. |
| `sys.align` | Layout | group (fields inherit) | Zero padding to a byte multiple. |
| `sys.crc` | Layout | group (fields inherit) | Appends/validates a CRC over a group. |

## Semantic (value) processors

### `sys.scale`

Converts between a raw integer and a scaled logical value: `logical = raw / scale`. Writing multiplies by `scale` and rounds (away from zero) to a `long`; reading divides and yields a `decimal`. A `null` value passes through.

| Property | Required | Description |
|----------|----------|-------------|
| `scale` | yes | Non-zero decimal factor. |

### `sys.map`

Maps values in the read direction: each property maps a source value (the property name) to a result value (the property value). The source values are parsed with the data type of the field (or the value type of the repeat/choice); the result values with the `to` type. Reading finds the entry by source value; writing finds it by result value. A source value that is not mapped gives the `default` result; without a default it is an error (the default cannot be written). Also usable as a `valueProcessor` to convert a referenced value to a repeat count or choice index. At least one mapping is required. Replaces the former `sys.enum`.

| Property | Required | Description |
|----------|----------|-------------|
| `to` | no | The data type of the result values (default `sys.string`; any registered data type). |
| `default` | no | The result for source values that are not mapped (parsed with the `to` type). |
| any other name | at least one | One mapping: the property name is the source value, the property value is the result value. |

Examples: an enumeration on an `int32` field: `1` = `Red`, `2` = `Green` reads 1 as `Red` and writes `Red` as 1. A status byte to a choice index: `to` = `sys.int32`, then `8` = `0`, `9` = `1`, ... maps the source value 8 to the index 0.

### `sys.bits`

Extracts a range of bits from an integer value: the physical value is the whole integer (usually a virtual or peeked field), the logical value is the bit range. Reading extracts the range (sign-extended for signed types); writing places the bits at the offset. The value type is the data type of the field (or the value type of the repeat/choice), a fixed-size integer of 1 to 8 bytes.

| Property | Required | Description |
|----------|----------|-------------|
| `bitoffset` | yes | Index of the first (lowest) bit of the range. |
| `bitlength` | yes | Number of bits in the range. The range must fit the data type. |

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

### `sys.bitslicer`

Joins the lowest `bits` bits of each byte of one field into a single unsigned value, and back (the within-one-field counterpart of `sys.bitpacker`). For example 7 bits of 2 bytes give a 14-bit value (MIDI pitch bend), 7 bits of 4 bytes a sync-safe integer. On read a byte with a bit set above `bits` is an error; on write the unused high bits are zero and a value that does not fit is an error. The field data type must be an integer that can hold `bits x byte count` bits (one less for signed types; at most 64 bits in total).

| Property | Required | Description |
|----------|----------|-------------|
| `bytelength` | one of | Number of bytes. Alternative to the field `byteLength` (both together is an error). |
| `bits` | no | Bits taken from each byte, 1 to 8 (default 7). |
| `byteorder` | no | `little` (default, least significant slice first) or `big`. |

## Packing processors

A packing processor is selected by a `packed` node (exactly one, in the group's `processors`; it is never inherited). It encodes the ordered logical values of the group's fields into one carrier of the group's `byteLength` bytes, and back. The fields may have value processors but no representation or layout processors; the carrier then goes through the layout chain once.

### `sys.bitpacker`

Packs the fields of a packed group into bit fields of arbitrary width, in declaration order. Each value is encoded with the default representation of its data type (little-endian, at most 8 bytes wide) and its lowest `bits` bits are stored; signed types are sign-extended on read. Bits that remain in the carrier are zero when writing and ignored when reading.

Writing and reading fail when `bits` is not between 1 and the width of the data type, when the bits of all fields together do not fit in the carrier (`byteLength` x 8), or when the data type has no fixed-width default representation of at most 8 bytes (strings and byte arrays are not supported). Writing also fails when a value does not fit in its `bits`; the engine reports the failed processor result.

| Property | Required | Description |
|----------|----------|-------------|
| `bits` | no | Field property: number of bits the field occupies (default: 1). Written `sys.bitpacker.bits` or `bits` on the field. |
| `bitorder` | no | Processor property: `little` (default) or `big` bit order within a byte. `little` fills each byte from the least significant bit, `big` from the most significant bit. |

```json
{
  "name": "Status",
  "kind": "packed",
  "byteLength": 1,
  "processors": [ { "name": "sys.bitpacker", "bitorder": "big" } ],
  "members": [
    { "name": "Enabled", "kind": "field", "datatype": "sys.bool", "bits": 1 },
    { "name": "Mode", "kind": "field", "datatype": "sys.uint8", "bits": 3 },
    { "name": "Level", "kind": "field", "datatype": "sys.uint8", "bits": 3,
      "processors": [ { "name": "sys.scale", "scale": 2 } ] },
    { "name": "Flag", "kind": "field", "datatype": "sys.bool" }
  ]
}
```

In the example `byteLength` could be left out (default 1) and `Flag` has no `bits` (default 1). Layout processors on the enclosing group (byte order, align, CRC) apply to the finished carrier, for example `sys.bytepacker` with `byteorder` `big` reverses a carrier of more than one byte.

## Layout processors

### `sys.bytepacker`

Lays out fields as whole bytes in declaration order, in the configured byte order. Values without a fixed width (strings) are passed on unchanged. Usually the head of a layout chain.

| Property | Required | Description |
|----------|----------|-------------|
| `endian` | no | Group property: `little` (default) or `big` byte order of fixed-width values. |

### `sys.align`

Pads with zero bytes so each value, and the end of the group, starts on a multiple of `bytes`. Usable as the layout head or chained after a head such as the byte packer. On a group that contains a packed group, the finished carrier is aligned like any other encoded value.

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

