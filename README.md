# Thinhnv Unity Tools

Editor utilities for Unity projects (`com.thinhnv.unitytools`, Unity 2021.3+).

## Install

Package Manager › *Add package from git URL…* with this repository's URL, or add it to
`Packages/manifest.json`:

```json
"com.thinhnv.unitytools": "https://github.com/thinhnv-percas/thinhnv-unity-tools.git"
```

## Fig Editor

Opens, renders and edits Figma `.fig` files inside Unity, imports and updates them from Figma links,
and exports layers into the game's textures. **Tools › Thinhnv › Fig Editor**. Full documentation:
[Editor/FigEditor/README.md](Editor/FigEditor/README.md).

## Agents (Claude Code and others)

Coding agents can get assets out of the designs, check where textures are used and apply them to
prefabs and scenes through the Fig Editor's agent bridge and the `fig_agent.py` helper:

1. Open the game project in Unity (the bridge runs in the open editor).
2. **Tools › Thinhnv › Fig Editor - Agent: Install Skill** copies the `unity-fig-assets` skill into
   `<project>/.claude/skills/` (`SKILL.md`, `fig_agent.py`, `REFERENCE.md`); commit it with the project.
3. For Figma updates, each user sets their Figma token in *Preferences › Thinhnv › Fig Editor*.

Agents then run `python3 .claude/skills/unity-fig-assets/fig_agent.py …` from the project folder.
Command reference: [Tools~/fig-agent/README.md](Tools~/fig-agent/README.md).

## Other tools

The `Editor/` folder also holds smaller utilities (each in its own folder or file): animation support,
asset child converter, build report, bulk rename, code executor, hierarchy customisation, material
reference replacer, mesh merge, player prefs editor, project curator and pin board, screenshot utility,
smart importer, scene switcher and camera tools, UI texture exporter, and others. Most add items under
**Tools › Thinhnv** or to the relevant inspector or context menu.
