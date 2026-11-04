---
name: unity-fig-assets
description: Get UI assets out of Figma / .fig design files into this Unity project and wire them up, with the Fig Editor (com.thinhnv.unitytools). Use when asked to find a layer in a design, preview it, export or re-export it as a texture / sprite, import or update a design from a Figma link, check which prefabs, scenes or materials use a texture (references, unused or missing textures, sprite import settings), or assign a sprite / texture to an Image, SpriteRenderer or RawImage. Also for "lấy asset từ Figma", "xuất ảnh", "cập nhật từ Figma", "check ref", "áp dụng vào Unity", "gán sprite".
---

# Figma / .fig assets in Unity

The project uses the Fig Editor (package `com.thinhnv.unitytools`). Its design files are `.fig` files in
the project; a **project config** asset (`FigEditorConfig.asset`) lists them with their Figma links and
**links layers to textures** (layer id → texture asset). All of it is driven through `fig_agent.py`,
next to this file:

```bash
python3 .claude/skills/unity-fig-assets/fig_agent.py <command> ...   # run from the Unity project folder
```

(If it's missing, it is in `Library/PackageCache/com.thinhnv.unitytools@*/Tools~/fig-agent/fig_agent.py`;
reinstall with the Unity menu *Tools › Thinhnv › Fig Editor - Agent: Install Skill*.)

Every command prints JSON; `"ok": false` comes with an `"error"` saying what to do. `REFERENCE.md`
next to this file lists every command and parameter.

## Two kinds of commands

- **Offline** (read the project's files, no Unity needed): `status`, `config`, `guid`, `where-used`,
  `refs`, `check`.
- **Editor** (`call <command> key=value ...`): run inside the **open Unity editor** through the Fig
  Editor's agent bridge (`Temp/FigAgent`). Needed for anything that reads a `.fig`, renders, writes
  textures, talks to Figma or edits prefabs / scenes. First run `call ping`. If it times out: Unity
  isn't open on this project, is compiling, is showing a modal dialog, or the bridge is off
  (*Preferences › Thinhnv › Fig Editor › Answer agent requests*). With MCP for Unity connected, run the
  menu item `Tools/Thinhnv/Fig Editor - Agent: Process Requests` (it answers waiting requests at once),
  and read the Unity console for compile errors.

## Workflow

1. **Orient.** `status`, then `config`: which design files exist, their Figma links, which layers are
   already linked to which textures.
2. **Find the layer.** `call find file=<name or path> query=<text>` (searches layer names and text
   content; `type=FRAME|TEXT|...` filters). For structure: `call list file=<f> depth=2`, then
   `call list file=<f> under=<frameId> depth=2` to go deeper. Prefer `find` on big files.
3. **Look before you export.** `call render file=<f> layer=<id> scale=2`, then open the returned `png`
   path and look at it. Confirm it is the right layer (state, size, no missing pieces) before writing
   anything into the project.
4. **Export into a texture.** `call export file=<f> layer=<id> texture=Assets/.../name.png [scale=2]`.
   - If that texture already exists it is **overwritten in place** (GUID, import settings and every
     reference stay) and linked to the layer. If not, it is created (sprite import by default,
     `sprite=false` for a plain texture).
   - **Re-use the existing texture** when the layer is already linked (`config` shows it): export to
     the same path; never create a second copy.
   - `fit=true` renders at the texture's current width (keeps a fixed-size asset fixed).
   - `call exportAll` re-exports every linked texture (unchanged images aren't rewritten).
5. **Check references.** `refs` (every linked texture and the prefabs / scenes / materials using it, with
   GameObject and component), `where-used <path|guid>` for one asset, `check` for problems: missing
   textures or .fig files, textures nothing uses, textures used as sprites but not imported as sprites,
   never-exported links. `call refs` does the same inside Unity through its asset database (also
   counts references the text search can't see, e.g. in binary assets).
6. **Apply in Unity.**
   - `call scan asset=Assets/.../Screen.prefab` lists every sprite / texture reference in the prefab
     (object path, component, property, current value, and the Figma layer when it is a linked texture).
     Without `asset` it scans the open scenes.
   - `call assign asset=<prefab> object=<path from scan> texture=<texture path>` puts the texture into
     the object's Image / SpriteRenderer (as a sprite) or RawImage (as a texture). `component=` and
     `property=` pick others; `nativeSize=true` resizes an Image to the sprite; a texture not imported
     as a sprite is switched to Sprite (`makeSprite=false` to refuse instead). Prefabs are saved; for
     an object of the open scene, the scene is marked dirty and saved only with `save=true`.
   - Verify: `where-used <texture>` shows the new user.
7. **Update from Figma** (needs the user's Figma token in Unity, never in chat or files):
   `call update file=<f> scope=linked` fetches only the frames holding linked layers and merges them;
   `scope=frames frames=1:2,3:4` chosen frames; `scope=whole` everything. Nothing is downloaded when
   Figma's version hasn't changed (`force=true` to download anyway). With *Export after update* on,
   linked textures are re-exported. `call import url=<figma link>` adds a new design file.

## Rules

- Don't edit `.fig`, `.prefab`, `.unity`, `.asset` or `.meta` files by hand; go through the commands.
  Never change a `.meta` GUID: references break.
- Keep one texture per layer: re-export to the linked texture instead of creating new ones.
- Before `assign`, `scan` the target and tell the user what will change (object, old → new value)
  unless they asked for exactly that change. Don't save scenes unless asked.
- Exports read the `.fig` saved on disk. If the user is editing the same file in the Fig Editor with
  unsaved changes, the console warns; ask them to save first.
- Report back plainly: which layers, which texture paths (created or overwritten, size), which prefabs
  or scenes changed, and any warnings from the results.
- If Figma calls fail with a token error, tell the user to set the token in *Preferences › Thinhnv ›
  Fig Editor* (scope "File content: read-only"); don't ask for it.
