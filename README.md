# Percas Unity Tools

Unity Editor utilities, distributed as a UPM package via Git URL.

## Install

In Unity: **Window > Package Manager > Add package from git URL...**

```
https://github.com/thinhnv-percas/thinhnv-unity-tools.git
```

## Tools

### Context Menu Buttons

`Editor/ContextMenuButtonEditor.cs` and `Editor/ScriptableObjectContextMenuButtonEditor.cs`

Generic inspectors for `MonoBehaviour` and `ScriptableObject` that render a button for every
parameterless method decorated with `[ContextMenu]`, so any script can expose one-click editor
actions without writing a custom `Editor` class. Buttons run across all selected objects when
multiple are selected.

### Fbx Editor

`Editor/Fbx/*` — menu **Percas > Fbx Tools > Fbx Editor**.

Opens a real `.fbx` file's node hierarchy inside Unity and edits it, then
**saves the changes back into the original `.fbx` file** using the Autodesk
FBX SDK bindings — not just into Unity's imported copy. Open a file via the
**Open...** button, or by dragging an `.fbx` onto the window — either a raw
file from Windows Explorer or an imported model asset from the Project
window both work. Three tabs on the right-hand panel, one per phase:

- **Transform** (Phase 1) — local translation/rotation/scale, rotation/scaling
  pivot, geometric offset, rename, reparent (drag-and-drop) and delete nodes.
  Also shows the mesh's **Mesh Bounds Center**, computed from its own vertex
  data — this is *not* the same thing as Rotation/Scaling Pivot or Geometric
  Offset (those are separate FBX properties most files never set; a centered-
  looking pivot in your DCC tool is often just where the vertex data sits, no
  property involved). Editing this field moves the mesh's control points and
  compensates the node's Translation so the object stays put in the scene —
  a "recenter pivot" action, not a write to the other three fields.
- **Material** (Phase 2) — reassign which scene material a node's material
  slot points to, and browse/replace a material's diffuse texture file.
- **Mesh** (Phase 3a + 3b) — a data-table of control points you can nudge by
  hand, a "Weld" action to merge nearby control points and rebuild polygon
  topology, "Recalculate Normals", and an "Edit In Scene View" toggle that
  draws the mesh as a wireframe with a pickable handle per control point plus
  a drag handle for the selected one, with its own Undo/Redo (scoped to
  SceneView drags only — it does not cover edits made in the table above).

**Setup:** this feature depends on Unity's official `com.unity.formats.fbx`
(FBX Exporter) package, declared in `package.json`. The first time you open
this package in the Unity Editor, open **Window > Package Manager** and
confirm `com.unity.formats.fbx` (and its `Autodesk.Fbx` bindings) resolved —
the pinned version in `package.json` may need adjusting to whatever your
Unity version's registry offers.

**Safety:**
- Every "Save" (overwriting the original file) first copies it into a
  `FbxToolBackups/` folder next to it, timestamped, before writing anything.
- Before the actual write, a dialog lists every change that will be applied
  so nothing is silently written.
- Files containing animation, skinning or blend shapes open read-only for
  saving — this tool cannot yet guarantee those round-trip without
  corruption, so Save is disabled rather than risking a silent bad write.
  Deleting/renaming a bone-like node, and vertex-editing a skinned mesh, both
  warn or block even outside that file-level check.
- **Reparent's "preserve world transform" is an approximation**: this
  package's `Autodesk.Fbx` binding doesn't expose the SDK's animation
  evaluator, so global transforms are composed by hand from each ancestor's
  local translation/rotation/scale — rotation/scaling pivots and pre/post-
  rotation on ancestor nodes are not accounted for. Fine for the common case;
  verify visually after reparenting a node whose ancestors have non-default
  pivots.
- **"Recenter pivot" (editing Mesh Bounds Center) has the same approximation**:
  it compensates the node's own Translation using only its Rotation/Scaling,
  ignoring its own rotation/scaling pivots and pre/post-rotation. Fine for the
  common case (a node with default pivots); verify visually otherwise. Blocked
  entirely on skinned meshes, same as the other vertex-editing operations.
- **Weld is scoped-down in this version**: it merges control points and
  rebuilds polygon topology, but does **not** preserve the mesh's existing UV
  or per-polygon material layers — welded geometry gets a single default
  material index and no UVs. Reassign the material afterward on the Material
  tab; normals are recalculated automatically right after a weld. Grouping is
  O(n²), so it's fine on modest meshes but will be slow on dense ones.
  Polygons that collapse to fewer than 3 distinct corners after merging are
  dropped, and the tool tells you how many.
- **The SceneView preview ignores the node's actual transform and any
  FBX↔Unity axis/unit conversion** — control points are drawn using their raw
  mesh-space coordinates directly as SceneView world coordinates, on purpose,
  so the tool never has to touch (or risk mis-converting) the node/scene
  transform just to draw a preview. It won't visually line up with an
  imported copy of the same file elsewhere in the scene; zoom/pan to it and
  treat it as an isolated shaping view, not an overlay.

**Known open item:** this code was written against the documented
`Autodesk.Fbx` API surface but has not yet been compiled inside a real Unity
Editor (no Unity install in the environment that authored it). Before relying
on it, fix any compile errors against the actual installed `com.autodesk.fbx`
assembly version, and test Open/edit/Save on a throwaway `.fbx` file first.

**Not yet implemented:** support for skinned/animated files (see the plan for
why that's deliberately out of scope for now, not an oversight).
