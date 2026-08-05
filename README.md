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
FBX SDK bindings — not just into Unity's imported copy. Three tabs on the
right-hand panel, one per phase:

- **Transform** (Phase 1) — local translation/rotation/scale, rotation/scaling
  pivot, geometric offset, rename, reparent (drag-and-drop) and delete nodes.
- **Material** (Phase 2) — reassign which scene material a node's material
  slot points to, and browse/replace a material's diffuse texture file.
- **Mesh** (Phase 3a) — a data-table of control points you can nudge by hand,
  a "Weld" action to merge nearby control points and rebuild polygon topology,
  and "Recalculate Normals". There is no SceneView vertex-dragging yet
  (Phase 3b in the plan) — everything here is numeric fields.

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
- **Weld is scoped-down in this version**: it merges control points and
  rebuilds polygon topology, but does **not** preserve the mesh's existing UV
  or per-polygon material layers — welded geometry gets a single default
  material index and no UVs. Reassign the material afterward on the Material
  tab; normals are recalculated automatically right after a weld. Grouping is
  O(n²), so it's fine on modest meshes but will be slow on dense ones.
  Polygons that collapse to fewer than 3 distinct corners after merging are
  dropped, and the tool tells you how many.

**Known open item:** this code was written against the documented
`Autodesk.Fbx` API surface but has not yet been compiled inside a real Unity
Editor (no Unity install in the environment that authored it). Before relying
on it, fix any compile errors against the actual installed `com.autodesk.fbx`
assembly version, and test Open/edit/Save on a throwaway `.fbx` file first.

**Not yet implemented:** Phase 3b (SceneView gizmo-based vertex dragging) and
support for skinned/animated files.
