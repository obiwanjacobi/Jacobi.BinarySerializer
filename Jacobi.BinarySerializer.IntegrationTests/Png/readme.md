# PNG Integration Test

Findings from modeling PNG (`png.json`) with the current schema/engine. To be reviewed.

## Covered by `png.json`

- Signature (as a big-endian `UInt64`), and one generic `Chunk`: `Length`, `Type` (4 byte ASCII string, the chunk type / FourCC), `Data` (a choice per chunk type: IHDR, PLTE, IDAT, IEND) and `Crc`.

## TODOs

- [x] **Generic chunk list.** `Chunk` is a repeat without a count (until the end of the input, must be the last node of its group). The root is a (Count=1) repeat too. `Data` has a `size` of `ref:Png.Chunk[].Length` (bytes of the payload); the palette/image data are count-less repeats that end at that window.
- [x] **Chunk type mapping.** `sys.map` converts the `Type` FourCC string to the choice index; `[]` refs address the current chunk instance.
- [ ] **Length-driven payload (`data[length]`).** No byte-array data type and no blob whose size comes from a preceding length field. Related to the existing TODO *Derive length prefixes and choice discriminators*.
- [x] **Choice by chunk type.** The choice index must be a constant or a published value; it cannot be selected from the chunk's `Type` string (`IHDR`, `PLTE`, `IDAT`, `IEND`, unknown). Custom Processor or will the EnumProcessor worK?
- [ ] **CRC32 field.** `Crc` is a plain `UInt32`, not derived. Needs a checksum processor covering `Type` + `Data` (a range of previous fields). Group-based checksum layout-processor -perhaps with multiple algorithms.
- [x] **Constant/expected values.** Nothing validates the signature (or other magic values) on read or supplies them on write.
- [ ] **Byte-array type for the signature.** The signature is modeled as a `UInt64` because there is no fixed-size byte-array type.
- [x] **Verify schema loading.** Done: `PngSchemaTests` loads `png.json`, compiles it and builds a plan (numeric property `length: 4` and the `byteorder` group property work).
