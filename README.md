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
