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
- **Board tab**: an interactive canvas for one page, laid out and operated like Figma, with the layer
  properties beside it (*Properties* toggle). See [Board](#board).
- **Images tab**: every `images/<sha1>` member with a preview, size, the layers that use it, export and
  replace. Replacing re-hashes the image (Figma names images by the SHA-1 of their bytes) and points
  every reference at the new hash.
- **File tab**: header / chunk / schema info, the thumbnail (preview, replace, export), `meta.json`
  (editable — `file_name` is the name Figma shows), other zip members, the message's document-level
  fields, and JSON / schema exports for inspection.

## Board

| Action | Input |
| --- | --- |
| Scroll / zoom | Wheel scrolls (Shift: sideways); Ctrl/Cmd/Alt + wheel zooms at the cursor |
| Pan | Space + drag, middle-drag |
| Zoom | Shift+1 fit page, Shift+2 or F selection, Shift+0 100% |
| Select | Click selects at the current level: a top-level layer, or a top-level frame's direct child; next to the selection, its siblings |
| Go deeper / up | Double-click enters one level; Esc selects the parent; Enter the first child; Ctrl/Cmd-click selects the deepest layer |
| Multi-select | Shift-click; drag a marquee on empty canvas or on an unselected top-level frame's background; Ctrl/Cmd+A selects siblings |
| Move | Drag (Shift locks the axis); smart guides snap to sibling and parent edges and centres (Ctrl/Cmd disables) |
| Resize | Drag a handle (Shift keeps the ratio, Alt from the centre) |
| Nudge | Arrows (Shift: 10 px) |
| Edit | Delete, Ctrl/Cmd+D duplicate, Ctrl/Cmd+Shift+H show/hide, right-click menu (order, zoom, copy GUID) |
| Text | Double-click selected text to edit it in the properties panel |
| Undo | Ctrl/Cmd+Z / Ctrl+Y (Unity's Undo drives the Fig Editor history), or the toolbar buttons |

*Snap px* rounds positions and sizes to whole pixels; *Guides* toggles smart guides.

What is drawn: frames, rectangles (with corner radii), ellipses, lines, solid / gradient (average
colour) / image fills, strokes, frame clipping, text in Unity's editor font (size, alignment, wrapping,
bold/italic), and vector layers (vectors, booleans, stars, polygons) from their `fillGeometry` /
`strokeGeometry` outlines with non-zero / even-odd fills. Not drawn: effects (shadows, blurs), blend
modes, gradients as gradients, masks, real fonts.

### Why it stays smooth

- **Scene cache** (`FigBoardScene`): the page is read from the file once into world-space items in
  paint order (transform, outline, bounds, paint). Moving a layer re-reads only that layer and carries
  its descendants along by the same transform change (a 20 000-layer frame moves in ~4 ms); a full
  rebuild happens only when the layer tree changes.
- **One GL batch** (`FigBoardRenderer`): fills, strokes and overlays go out as one batch of triangles,
  split only where a texture must be drawn in between to keep the paint order, instead of one
  immediate-mode call per layer.
- **Vector raster cache**: vector layers are rasterized (anti-aliased scanline, 4× supersampled) into
  textures per layer and zoom bucket (powers of two). Rasterizing gets at most 8 ms per frame; the rest
  continues next frame showing the previous bucket meanwhile. An LRU budget (24 MP) evicts unused ones;
  cache keys follow the geometry blobs, so rebuilding the scene keeps them.
- **Lazy images**: at most two images decode per frame.
- **Culling and level of detail**: layers off screen (or clipped away by their frame) and under half a
  pixel are skipped, tiny vectors draw as a box, tiny text as a bar.
- Everything is clipped on the CPU to the board and to clipping frames.

## Undo

Board edits, the quick properties (name, visibility, lock, opacity, size, position, text, fill/stroke
colours), reordering, duplicating and deleting are undoable. Consecutive edits of the same thing within a
second merge into one step (dragging a slider is one undo). Edits made in the raw *All fields* view are
not recorded.

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
| `FigBoardScene.cs` | Cached world-space scene of a page: placement, paint, hit tests, fast re-placement |
| `FigBoardRenderer.cs` | Board drawing: GL batching, vector raster cache, images, text, clipping |
| `FigGeometry.cs` | Affine transforms, path blobs, shapes, clipping, the rasterizer |
| `FigUndo.cs`, `FigUndoProxy.cs` | Undo history and its bridge to Unity's Undo |
| `FigEditorWindow*.cs` | The window: tree, inspector, board, images, file |

Format knowledge comes from [figcraft](https://github.com/storytold/figcraft)
(`crates/format/src/fig*`, MIT / Apache-2.0).

## Caveats

- Figma's `.fig` format is private and changes without notice; this reads it by field name through the
  file's own schema, so it keeps working as long as the container stays the same.
- Derived data is not recomputed. Editing text through the *Text* box keeps `textData` consistent
  (`characterStyleIDs`, `lines`) and drops `derivedTextData` so Figma lays the text out again; changing
  a vector's size doesn't rebuild its outline (`fillGeometry`, `vectorData`).
- Resizing on the board drops the layer's cached `fillGeometry` / `strokeGeometry` (Figma rebuilds them
  from the size); vectors, booleans and groups can be moved but not resized there. Resizing text switches
  it to auto height with a fixed width (fixed size when the height changed) so Figma keeps the new box.
- Duplicating a layer gives the copies fresh GUIDs; references to layers inside the copy (instance
  overrides keyed by GUID path, prototype links) still point at the originals.
- Deleting a component leaves its instances without a main component.
- Zip archives over 4 GB and zstd dictionaries are not supported.
