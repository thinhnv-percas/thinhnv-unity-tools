# Fig Editor

Open, inspect and edit Figma `.fig` files (Figma: *File > Save local copy…*) inside the Unity Editor.

Open via **Tools > Thinhnv > Fig Editor**, right-click a `.fig` asset > **Open in Fig Editor**, or drop a
`.fig` file onto the empty window.

## What it does

- **Layer tree**: every layer of the document (pages, frames, groups, text, shapes, components,
  instances…), in Figma's order. Search by name, or with `type:TEXT`, `id:1:23`, `text:hello`,
  `img:<hash prefix>`. Right-click a row for expand/collapse all, copy GUID, move, duplicate, delete.
- **Layer tab**: name, visible, locked, opacity, size, position, text content and solid fill/stroke
  colours on top; below that, *All fields* shows every field of the layer as the file's schema defines
  it. Any field can be edited, removed, or added (*+ Add field*); arrays can grow and shrink. Colours,
  vectors and GUIDs get inline controls, byte arrays can be exported/imported, `…Blob` indices can
  export the blob they point at.
- **Preview tab**: a wireframe of the selected frame or page from each layer's `transform` and `size`,
  filled with its top visible solid fill or image. Click selects, double-click re-roots, wheel zooms,
  right/middle/Alt-drag pans. It is a layout check, not a renderer (vectors draw as their bounds,
  strokes and effects are skipped).
- **Images tab**: every `images/<sha1>` member with a preview, size, the layers that use it, export and
  replace. Replacing re-hashes the image (Figma names images by the SHA-1 of their bytes) and points
  every reference at the new hash.
- **File tab**: header / chunk / schema info, the thumbnail (preview, replace, export), `meta.json`
  (editable — `file_name` is the name Figma shows), other zip members, the message's document-level
  fields, and JSON / schema exports for inspection.

**Save** overwrites the file (a `.bak` copy of the original is written first, once per session);
**Save As** writes a new file. Edits live in memory until saved and are lost on a script reload unless
saved when the window asks.

## How the format is handled

A `.fig` file is a zip holding `canvas.fig`, `meta.json`, `thumbnail.png` and `images/<sha1>`.
`canvas.fig` (also accepted on its own) is an 8-byte `fig-…` magic, a `u32` version, then
length-prefixed chunks, each raw-deflate or zstd compressed: a binary [kiwi](https://github.com/evanw/kiwi)
schema, then a message encoded with it. The message lists `nodeChanges` (one per layer, each with its
GUID, its parent's GUID and a position string that orders siblings) and `blobs`.

Every file carries its own schema, so the message is decoded generically (`FigKiwi.cs`) and saving
re-encodes it with that same schema: fields the tool has no UI for are written back unchanged, and an
unedited file re-encodes byte for byte. Chunks are written raw-deflated (as figcraft's exporter does);
zip members other than `canvas.fig` are kept byte for byte.

| File | Role |
| --- | --- |
| `FigFile.cs` | Container, layer index, edits (text, duplicate, delete, reorder, image replace), save |
| `FigKiwi.cs` | Kiwi schema + message decoder/encoder, default values, JSON dump |
| `FigZstd.cs` | Self-contained zstd decoder (RFC 8878) for the message chunk of newer files |
| `FigZip.cs` | Minimal zip reader/writer, CRC-32, shared helpers |
| `FigEditorWindow*.cs` | The window: tree, inspector, preview, images, file |

Format knowledge comes from [figcraft](https://github.com/storytold/figcraft)
(`crates/format/src/fig*`, MIT / Apache-2.0).

## Caveats

- Figma's `.fig` format is private and changes without notice; this reads it by field name through the
  file's own schema, so it keeps working as long as the container stays the same.
- Derived data is not recomputed. Editing text through the *Text* box keeps `textData` consistent
  (`characterStyleIDs`, `lines`) and drops `derivedTextData` so Figma lays the text out again; changing
  a vector's size doesn't rebuild its outline (`fillGeometry`, `vectorData`).
- Duplicating a layer gives the copies fresh GUIDs; references to layers inside the copy (instance
  overrides keyed by GUID path, prototype links) still point at the originals.
- Deleting a component leaves its instances without a main component.
- Zip archives over 4 GB and zstd dictionaries are not supported.
