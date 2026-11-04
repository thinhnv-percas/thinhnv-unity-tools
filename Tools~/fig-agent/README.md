# fig-agent: the Fig Editor for coding agents

`fig_agent.py` lets a coding agent (Claude Code or any other) get assets out of the project's Figma /
`.fig` design files into Unity, check which assets reference them, and apply them to prefabs and
scenes. Python 3 standard library only; run it from the Unity project folder (or pass `--project`).

The skill for Claude Code (`skill/unity-fig-assets/SKILL.md`) describes the workflow. Install it into a
game project with the Unity menu **Tools › Thinhnv › Fig Editor - Agent: Install Skill** (or
*Preferences › Thinhnv › Fig Editor › Install agent skill*): it copies `SKILL.md`, `fig_agent.py` and this
file (as `REFERENCE.md`) into `<project>/.claude/skills/unity-fig-assets/`.

## How it works

- **Offline commands** read the project's files: the project config asset (`FigEditorConfig.asset`, Unity
  YAML), `.meta` GUIDs, and the text of prefabs, scenes, materials, sprite atlases, animations… to find
  references. No Unity needed.
- **Editor commands** (`call`) go through the Fig Editor's **agent bridge** in the open Unity editor: a
  JSON request written to `Temp/FigAgent/requests/<name>.json` is run on the editor's main thread and
  the result written to `Temp/FigAgent/results/<name>.json`; `fig_agent.py call` writes the request and
  waits for the result. The editor writes a heartbeat to `Temp/FigAgent/alive.json` every 5 s. The
  bridge polls twice a second; the menu item **Tools › Thinhnv › Fig Editor - Agent: Process Requests**
  (for MCP for Unity's `execute_menu_item`) answers at once. It can be switched off in
  *Preferences › Thinhnv › Fig Editor › Answer agent requests*.

Values in `key=value` are parsed as JSON when they can be (`scale=2`, `fit=true`), else taken as text;
`--json '{...}'` passes them as one object.

## Offline commands

| Command | Does |
| --- | --- |
| `status` | Whether the editor bridge is answering, the config, the design files |
| `config` | The project config: import / texture folders, design files (path, exists, Figma link, version) and their layer → texture links (texture path, GUID, scale, fit, last export) |
| `guid <path\|guid>` | An asset's GUID, or a GUID's asset path |
| `where-used <path\|guid>` | Files referencing the asset, with GameObject, component (Image, SpriteRenderer, script names…) and property |
| `refs` | Every linked texture with its users, `unused` flags, and links whose texture is missing |
| `check` | Problems: missing .fig files or textures, files without a Figma link, textures nothing uses, textures used as sprites but not imported as sprites, links never exported |

## Editor commands (`call <command> ...`)

`file` is a design file's name in the config or a `.fig` path; `layer` a layer id (`12:34`) or a unique
layer name.

| Command | Parameters | Result |
| --- | --- | --- |
| `ping` | | Unity version, project, config path, whether a Figma token is set |
| `config` | | The project config as Unity sees it (textures by asset path and GUID) |
| `list` | `file`, `depth` (2), `page` (name or id), `under` (layer id) | Pages and layers: id, name, type, page-space x / y / w / h, text, linked texture, `childCount` below the depth |
| `find` | `file`, `query` (name / text / id), `type`, `limit` (50) | Matching layers with their page and name path |
| `render` | `file`, `layer`, `scale` (1), `out` (default `Temp/FigAgent/renders/…png`) | A PNG of the layer (with effects, on transparent): `png`, `width`, `height` |
| `export` | `file`, `layer`, `texture` (`Assets/….png`), `scale`, `fit`, `sprite` | Creates the texture or overwrites the existing one in place, and links it to the layer in the config |
| `exportAll` | `file` (optional), `force` | Re-exports linked textures (unchanged images aren't rewritten); details in the console |
| `refs` | `texture` (path or GUID, optional) | Users of linked textures (or of one asset) through the asset database, missing links |
| `scan` | `asset` (prefab; omit for the open scenes) | Every sprite / texture reference: object path, component, property, value, linked Figma layer |
| `assign` | `asset` (prefab; omit for the open scene), `object`, `texture`, `component`, `property`, `nativeSize`, `makeSprite` (true), `save` | Sets an Image / SpriteRenderer sprite or RawImage texture (or the given component / property); returns the previous value |
| `update` | `file`, `scope` (`whole`, `linked`, `frames`), `frames` (`1:2,3:4`), `force` | Updates the .fig from its Figma link (only the chosen frames for `linked` / `frames`, images it already has are reused); reports bytes, images, warnings |
| `import` | `url`, `onlyLinked`, `folder`, `name`, `replace` | Imports a Figma link as a new .fig |

`update` and `import` need the user's Figma personal access token, set in Unity
(*Preferences › Thinhnv › Fig Editor*) or in the `FIGMA_TOKEN` environment variable of the editor.

## Examples

```bash
fig_agent.py status
fig_agent.py call find file=Main query="play button"
fig_agent.py call render file=Main layer=12:34 scale=2          # then look at the PNG
fig_agent.py call export file=Main layer=12:34 texture=Assets/UI/Home/play.png scale=2
fig_agent.py where-used Assets/UI/Home/play.png
fig_agent.py call scan asset=Assets/UI/Home.prefab
fig_agent.py call assign asset=Assets/UI/Home.prefab object=Canvas/Buttons/Play texture=Assets/UI/Home/play.png nativeSize=true
fig_agent.py call update file=Main scope=linked
fig_agent.py check
```
