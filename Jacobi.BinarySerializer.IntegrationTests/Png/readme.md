# PNG Integration Test

Findings from modeling PNG (`png.json`) with the current schema/engine. To be reviewed.

## Covered by `png.json`

- Signature (an 8 byte `Bytes` field with the magic constant), and one generic `Chunk`: `Length`, `Type` (4 byte ASCII string, the chunk type / FourCC), `Data` (a choice per chunk type: IHDR, PLTE, IDAT, IEND; palette and image data are length-less `Bytes` fields that take the rest of the `Data` size window) and `Crc`.

## TODOs

- [x] **Generic chunk list.** `Chunk` is a repeat without a count (until the end of the input, must be the last node of its group). The root is a (Count=1) repeat too. `Data` has a `size` of `ref:Png.Chunk[].Length` (bytes of the payload); the palette/image data are count-less repeats that end at that window.
- [x] **Chunk type mapping.** `sys.map` converts the `Type` FourCC string to the choice index; `[]` refs address the current chunk instance.
- [x] **Length-driven payload (`data[length]`).** Bounded by `size` on the `Data` group (read window, derived `Length` on write). The engine now has a `Bytes` type (a length-less `Bytes` takes the rest of the window), see the next item.
- [x] **Use `Bytes` in `png.json`.** The signature is `Bytes` (`length: 8`, value `0x89504E470D0A1A0A`) and the `Palette`/`ImageData` payloads are length-less `Bytes` inside the sized `Data` choice. `Read_Grayscale8_ReadsEveryChunk` checks the signature and the payload.
- [x] **Choice by chunk type.** The choice index must be a constant or a published value; it cannot be selected from the chunk's `Type` string (`IHDR`, `PLTE`, `IDAT`, `IEND`, unknown). Custom Processor or will the EnumProcessor worK?
- [ ] **CRC32 field.** `Crc` is a plain `UInt32`, not derived. Needs a checksum processor covering `Type` + `Data` (a range of previous fields). Group-based checksum layout-processor -perhaps with multiple algorithms.
- [x] **Constant/expected values.** Nothing validates the signature (or other magic values) on read or supplies them on write.
- [x] **Byte-array type for the signature.** Done: `Bytes` with `length` and a hex constant.
- [x] **Verify schema loading.** Done: `PngSchemaTests` loads `png.json`, compiles it and builds a plan (numeric property `length: 4` and the `byteorder` group property work).
