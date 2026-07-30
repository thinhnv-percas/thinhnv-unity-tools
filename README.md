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

### Object Definition Builder

`Editor/ObjectDefBuilder/` — **Tools > Thinhnv > Object Definition Builder**

Project-specific (Smash Market). Drag a model, a fractured model and a texture per size; the tool
generates the object prefabs (`ObjectElement`), the shatter prefabs (`BreakPieceEffect`, plus one prefab
variant per stretched axis), the per-size materials, and writes them all into an `ObjectDefinitionSO`.
Every dragged source is kept in an `ObjectDefBuilderCacheSO` asset so a definition can be rebuilt later.

Because this package cannot reference the predefined `Assembly-CSharp`, the game types are reached
late-bound through `SmashMarketBridge` — the window shows an error box instead of compiling against them
when they are absent, so the package still builds in projects that do not have them.

Full documentation: `Doc/ObjectDefinitionBuilder.md` in the Smash Market repository.
