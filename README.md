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

### Fbx Editor (Phase 1: transform/pivot/hierarchy)

`Editor/Fbx/*` — menu **Percas > Fbx Tools > Fbx Editor**.

Opens a real `.fbx` file's node hierarchy inside Unity and lets you edit local
translation/rotation/scale, rotation/scaling pivot, geometric offset, rename,
reparent (drag-and-drop) and delete nodes — then **saves the changes back into
the original `.fbx` file** using the Autodesk FBX SDK bindings, not just into
Unity's imported copy.

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
  (rename, transform/pivot value, reparent, delete) so nothing is silently
  written.
- Files containing animation, skinning or blend shapes open read-only for
  saving — this phase cannot yet guarantee those round-trip without
  corruption, so Save is disabled rather than risking a silent bad write.
  Deleting/renaming a bone-like node still warns even outside that block,
  since Unity's own Animator/Avatar can reference bone names from outside
  the `.fbx` file.

**Known open item:** this code was written against the documented
`Autodesk.Fbx` API surface but has not yet been compiled inside a real Unity
Editor (no Unity install in the environment that authored it). Before relying
on it, open the package in Unity, fix any compile errors against the actual
installed `com.autodesk.fbx` assembly version, and test Open/edit/Save on a
throwaway `.fbx` file first.

**Not yet implemented (see plan for later phases):** material/texture
reassignment, mesh/vertex editing, and support for skinned/animated files.
