# Fig Editor

Open, inspect and edit Figma `.fig` files (Figma: *File > Save local copy…*) inside the Unity Editor.

Open via **Tools > Thinhnv > Fig Editor**, right-click a `.fig` asset > **Open in Fig Editor**, or drop a
`.fig` file onto the empty window.

## Layout

The window follows figcraft's (and Figma's) editor layout:

- **Rail** (left edge): *Layers*, *Assets*, *File*.
- **Left panel**: the file menu (open, recent, save, save as, reveal, close), the file name and its saved
  state, then the panel the rail picked:
  - *Layers*: the pages, then the layer tree of the current page in Figma's order (topmost first),
    with type icons, hover eye / lock buttons, double-click or F2 to rename, arrow-key navigation and a
    right-click menu (rename, select, zoom to, order, duplicate, delete, copy GUID). Layers inside an
    instance are listed in purple and read-only. Search by name, or with `type:TEXT`, `id:1:23`,
    `text:hello`, `img:<hash prefix>`.
  - *Assets*: the file's components (click to select and zoom) and images (preview, size, users,
    export, replace; replacing re-hashes the image and repoints every reference).
  - *File*: header, chunk and schema info, thumbnail, `meta.json` (editable), other zip members,
    document-level fields, JSON / schema exports.
- **Canvas**: the page rendered like Figma renders it (see [Canvas](#canvas)), frame titles, selection
  and hover outlines, a size badge, smart guides and the floating tool bar.
- **Right panel**: undo / redo, the zoom menu, *Save*, and three tabs:
  - *Design*: the selection's header (instances link to their main component), alignment, position and
    rotation, size with ratio lock and *Clip content*, an auto layout summary, layer opacity, blend mode
    and corner radii, typography (text, font, size, line height, letter spacing, alignment, resizing),
    fills and strokes (solid with hex and opacity, gradient stops, image scale mode; add, remove,
    hide), stroke weight and alignment, effects with their parameters, and PNG export at 0.5×–4×.
  - *Prototype*: the frame's interactions and the page's flows (read-only).
  - *Inspect*: every field of the layer as the file's schema defines it. Fields can be edited, removed
    or added, arrays can grow and shrink; colours, vectors and GUIDs get inline controls, byte arrays
    and blobs can be exported or imported. A layer inside an instance shows its override rows instead.

The colours follow the editor skin: dark like figcraft with the Pro skin, a light palette otherwise.

## Canvas

| Action | Input |
| --- | --- |
| Tools | V move, H hand, F frame, R rectangle, O ellipse, L line, T text (or the bottom tool bar); Esc back to move |
| Scroll / zoom | Wheel scrolls (Shift: sideways); Ctrl/Cmd/Alt + wheel zooms at the cursor; zoom menu in the header |
| Pan | Space + drag, middle-drag, hand tool |
| Zoom | Shift+1 fit page, Shift+2 selection, Shift+0 100% |
| Select | Click selects at the current level: a top-level layer, or a top-level frame's direct child; next to the selection, its siblings |
| Go deeper / up | Double-click enters one level; Esc selects the parent; Enter the first child; Ctrl/Cmd-click selects the deepest layer |
| Multi-select | Shift-click; drag a marquee on empty canvas or on an unselected top-level frame's background; Ctrl/Cmd+A selects siblings |
| Move | Drag (Shift locks the axis); smart guides snap to sibling and parent edges and centres (Ctrl/Cmd disables) |
| Resize | Drag a handle (Shift keeps the ratio, Alt from the centre) |
| Create | Drag with a shape tool, or click for a 100×100 layer, inside the frame under the cursor; the text tool opens an editor |
| Nudge | Arrows (Shift: 10 px) |
| Edit | Delete, Ctrl/Cmd+D duplicate, Ctrl/Cmd+Shift+H show/hide, right-click menu |
| Text | Double-click (or Enter on) a text layer to edit it in place; Esc or a click outside commits |
| Undo | Ctrl/Cmd+Z / Ctrl+Y (Unity's Undo drives the Fig Editor history), or the header buttons |

### What is rendered

The canvas is a software renderer (`FigRenderer`) that follows figcraft's renderer:

- Shapes from `fillGeometry` / `strokeGeometry` outlines (non-zero and even-odd), or from the size and
  corner radii for primitives; strokes inside, centre and outside.
- Solid, linear, radial, angular and diamond gradients through the paint transform; image fills (fill,
  fit, crop, tile, rotation) with mipmapped bilinear sampling; paint opacity and visibility.
- Drop and inner shadows (with spread), layer and background blur.
- Layer opacity and blend modes (normal, multiply, screen, darken, lighten, overlay) on isolated
  groups, frame clipping with rounded corners, masks, boolean layers from their outlines.
- Text from Figma's laid-out glyph outlines (`derivedTextData`), so it looks the same as in Figma
  without the fonts. Text without them (new or edited here) is drawn in an OS font close to the one
  named.
- Instances are expanded the way figcraft expands them: override rows from `symbolOverrides` and
  `derivedSymbolData` matched by GUID path (with override-key aliases), component properties (text,
  visibility, instance swap), nested instances, and constraints when an instance is resized.

### Why it stays smooth

- **Scene snapshot** (`FigScene`): the page is read once into world-space nodes in paint order
  (transform, outlines, paints, effects, bounds). An edit re-reads only that layer and carries its
  descendants along by the same transform change, copy-on-write, and reports the dirty region.
- **Tiles** (`FigTileCache`): the canvas is cut into 256 px tiles rendered on worker threads from the
  immutable snapshot, nearest to the centre first. Only tiles under a dirty region are re-rendered;
  while zooming, the previous zoom's tiles are drawn scaled until the new ones are ready (requests wait
  for the zoom to settle). Up to 600 tiles are kept, textures are pooled.
- **Lifted drag**: when a drag starts, the moved layers are rendered once into their own texture and
  the tiles beneath are rendered without them; during the drag only that texture moves. On release the
  file is updated, the tiles under the old and new place re-render, and the lifted copy stays until
  they are ready, so nothing flickers.
- **Lazy images**: images decode on first use, two per frame, with a mip chain for zoomed-out drawing.

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
| `FigView.cs` | A layer read through the instance override rows that apply to it |
| `FigScene.cs` | World-space scene snapshot of a page: instance expansion, paints, hit tests, copy-on-write refresh |
| `FigPath.cs`, `FigPaint.cs` | Path blobs and shapes; paints, gradients, effects, decoded images |
| `FigRaster.cs`, `FigRenderer.cs` | Coverage rasterizer, blur; the software renderer |
| `FigTileCache.cs` | Threaded tile cache, lifted drag layer, image decoding |
| `FigGeometry.cs` | Affine transforms and rectangle helpers |
| `FigTheme.cs` | Colours, styles and the vector icons |
| `FigUndo.cs`, `FigUndoProxy.cs` | Undo history and its bridge to Unity's Undo |
| `FigEditorWindow*.cs` | The window: layout, layers, canvas, Design / Prototype / Inspect, assets, undo |

Format knowledge comes from [figcraft](https://github.com/storytold/figcraft)
(`crates/format/src/fig*` for the format, its renderer and instance expansion for drawing;
MIT / Apache-2.0).

## Caveats

- Figma's `.fig` format is private and changes without notice; this reads it by field name through the
  file's own schema, so it keeps working as long as the container stays the same.
- Derived data is not recomputed. Editing text through the *Text* box keeps `textData` consistent
  (`characterStyleIDs`, `lines`) and drops `derivedTextData` so Figma lays the text out again; changing
  a vector's size doesn't rebuild its outline (`fillGeometry`, `vectorData`).
- Auto layout is shown but not recomputed: resizing or editing a layer inside an auto layout frame
  doesn't move its siblings until Figma lays the frame out again.
- Background blur only sees what lies within a tile's margin, so very large background blurs can show
  tile seams.
- Layers inside instances are read-only; edit the main component.
- Resizing on the canvas drops the layer's cached `fillGeometry` / `strokeGeometry` (Figma rebuilds them
  from the size); vectors, booleans and groups can be moved but not resized there. Resizing text switches
  it to auto height with a fixed width (fixed size when the height changed) so Figma keeps the new box.
- Duplicating a layer gives the copies fresh GUIDs; references to layers inside the copy (instance
  overrides keyed by GUID path, prototype links) still point at the originals.
- Deleting a component leaves its instances without a main component.
- Zip archives over 4 GB and zstd dictionaries are not supported.
