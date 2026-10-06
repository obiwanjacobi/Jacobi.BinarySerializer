# PNG Integration Test

Findings from modeling PNG (`png.json`) with the current schema/engine. To be reviewed.

## Covered by `png.json`

- Signature (as a big-endian `UInt64`), and the IHDR chunk: `Length`, `Type` (4 byte ASCII string), `Data` fields and `Crc`.

## TODOs

- [ ] **Generic chunk list.** No repeat-until-end (or until a given byte count/end of input). Only a constant count or a node-ref/pub-ref (a count value, not a byte length) works. Make count optional and validate it is the last repeat? Also: repeat until N bytes are consumed (byte-length based repeat) is not supported.
- [ ] **Length-driven payload (`data[length]`).** No byte-array data type and no blob whose size comes from a preceding length field. Related to the existing TODO *Derive length prefixes and choice discriminators*.
- [x] **Choice by chunk type.** The choice index must be a constant or a published value; it cannot be selected from the chunk's `Type` string (`IHDR`, `PLTE`, `IDAT`, `IEND`, unknown). Custom Processor or will the EnumProcessor worK?
- [ ] **CRC32 field.** `Crc` is a plain `UInt32`, not derived. Needs a checksum processor covering `Type` + `Data` (a range of previous fields). Group-based checksum layout-processor -perhaps with multiple algorithms.
- [x] **Constant/expected values.** Nothing validates the signature (or other magic values) on read or supplies them on write.
- [ ] **Byte-array type for the signature.** The signature is modeled as a `UInt64` because there is no fixed-size byte-array type.
- [x] **Verify schema loading.** Done: `PngSchemaTests` loads `png.json`, compiles it and builds a plan (numeric property `length: 4` and the `byteorder` group property work).
