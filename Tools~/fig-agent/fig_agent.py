#!/usr/bin/env python3
"""fig_agent: let a coding agent use the Fig Editor (com.thinhnv.unitytools) of a Unity project.

Two kinds of commands:

  * Editor commands go through the Fig Editor's agent bridge in the *open* Unity editor
    (Temp/FigAgent/requests -> results): read .fig files, render a layer to PNG, export layers
    into textures, update from Figma, find texture users, assign sprites to prefabs / scenes.

        fig_agent.py call ping
        fig_agent.py call find file=Main query=play
        fig_agent.py call render file=Main layer=12:34 scale=2
        fig_agent.py call export file=Main layer=12:34 texture=Assets/UI/play.png
        fig_agent.py call assign asset=Assets/UI/Home.prefab object=Canvas/Play texture=Assets/UI/play.png

  * Offline commands read the project's files directly (no Unity needed): the project config,
    GUIDs, and which prefabs / scenes / materials reference a texture.

        fig_agent.py config
        fig_agent.py where-used Assets/UI/play.png
        fig_agent.py refs
        fig_agent.py check

Output is JSON on stdout (exit code 1 when "ok" is false). Run from the Unity project folder or pass
--project. Standard library only.
"""

import argparse
import json
import os
import re
import sys
import time
import uuid
from pathlib import Path

BRIDGE = Path("Temp") / "FigAgent"
USER_EXTENSIONS = {".prefab", ".unity", ".mat", ".asset", ".spriteatlas", ".spriteatlasv2", ".controller",
                   ".overrideController", ".anim", ".playable", ".guiskin", ".uss", ".uxml"}
CLASS_NAMES = {"1": "GameObject", "4": "Transform", "224": "RectTransform", "114": "MonoBehaviour",
               "212": "SpriteRenderer", "21": "Material", "23": "MeshRenderer", "137": "SkinnedMeshRenderer",
               "222": "CanvasRenderer", "199": "ParticleSystemRenderer", "1001": "PrefabInstance"}
SLOW_COMMANDS = {"update", "import", "exportAll", "refs"}


# ─── Project ──────────────────────────────────────────────────────────────────

def find_project(start):
    p = Path(start).resolve()
    for candidate in [p, *p.parents]:
        if (candidate / "Assets").is_dir() and (candidate / "ProjectSettings").is_dir():
            return candidate
    sys.exit(f"Not inside a Unity project (no Assets/ and ProjectSettings/ above {p}); pass --project.")


def out(obj):
    print(json.dumps(obj, indent=2, ensure_ascii=False))
    return 0 if obj.get("ok", True) else 1


# ─── Editor bridge ────────────────────────────────────────────────────────────

def parse_value(text):
    try:
        return json.loads(text)
    except ValueError:
        return text


def bridge_alive(project):
    alive = project / BRIDGE / "alive.json"
    if not alive.exists():
        return False, "no heartbeat (Unity isn't open on this project, the package isn't installed, or scripts don't compile)"
    age = time.time() - alive.stat().st_mtime
    try:
        enabled = json.loads(alive.read_text(encoding="utf-8")).get("enabled", True)
    except ValueError:
        enabled = True
    if not enabled:
        return False, "the agent bridge is off (Preferences > Thinhnv > Fig Editor)"
    if age > 30:
        return False, f"last heartbeat {int(age)} s ago (Unity is closed, busy compiling, or paused in a modal dialog)"
    return True, None


def cmd_call(project, args):
    request = {}
    if args.json:
        request.update(json.loads(args.json))
    for item in args.params:
        if "=" not in item:
            sys.exit(f"Parameters are key=value (got {item!r}).")
        key, value = item.split("=", 1)
        request[key] = parse_value(value)
    request["command"] = args.command

    alive, why = bridge_alive(project)
    if not alive and not args.no_wait_check:
        print(f"warning: {why}; waiting anyway", file=sys.stderr)

    name = f"{args.command}-{uuid.uuid4().hex[:8]}.json"
    requests = project / BRIDGE / "requests"
    results = project / BRIDGE / "results"
    requests.mkdir(parents=True, exist_ok=True)
    temp = requests / (name + ".tmp")
    temp.write_text(json.dumps(request), encoding="utf-8")
    temp.replace(requests / name)

    timeout = args.timeout or (900 if args.command in SLOW_COMMANDS else 120)
    deadline = time.time() + timeout
    result_file = results / name
    while time.time() < deadline:
        if result_file.exists():
            try:
                result = json.loads(result_file.read_text(encoding="utf-8"))
            except ValueError:
                time.sleep(0.2)
                continue
            result_file.unlink()
            return out(result)
        time.sleep(0.25)

    pending = requests / name
    picked = not pending.exists()
    if not picked:
        pending.unlink()
    return out({"ok": False, "error": f"No answer from Unity after {timeout} s"
                + ("" if picked else f" and the request was never picked up ({why or 'editor busy?'})")
                + ". If MCP for Unity is connected, run the menu item "
                  "'Tools/Thinhnv/Fig Editor - Agent: Process Requests' and retry."})


# ─── Unity YAML (enough for the config asset and reference lookups) ───────────

def meta_guid(asset_path):
    meta = Path(str(asset_path) + ".meta")
    if not meta.exists():
        return None
    m = re.search(r"^guid:\s*([0-9a-f]{32})", meta.read_text(encoding="utf-8", errors="replace"), re.M)
    return m.group(1) if m else None


def guid_index(project):
    """guid -> asset path for Assets/ and Packages/ (embedded) and the package cache."""
    index = {}
    roots = [project / "Assets", project / "Packages", project / "Library" / "PackageCache"]
    for root in roots:
        if not root.is_dir():
            continue
        for dirpath, dirnames, filenames in os.walk(root):
            dirnames[:] = [d for d in dirnames if not d.endswith("~") and not d.startswith(".")]
            for f in filenames:
                if f.endswith(".meta"):
                    p = Path(dirpath) / f
                    g = meta_guid(p.with_suffix(""))
                    if g:
                        index[g] = p.with_suffix("").relative_to(project).as_posix()
    return index


def parse_scalar(text):
    text = text.strip()
    if text.startswith("{") and text.endswith("}"):
        result = {}
        for part in text[1:-1].split(","):
            if ":" in part:
                k, v = part.split(":", 1)
                result[k.strip()] = parse_scalar(v)
        return result
    if len(text) >= 2 and text[0] == text[-1] and text[0] in "'\"":
        inner = text[1:-1]
        return inner.replace("''", "'") if text[0] == "'" else bytes(inner, "utf-8").decode("unicode_escape")
    if re.fullmatch(r"-?\d+", text):
        return int(text)
    if re.fullmatch(r"-?\d*\.\d+(e-?\d+)?", text):
        return float(text)
    return text


def parse_unity_yaml(text):
    """A MonoBehaviour document's fields as nested dicts / lists (block style, as Unity writes it)."""
    lines = [l for l in text.splitlines() if l.strip() and not l.startswith("%") and not l.startswith("---")]

    def block(i, indent):
        # A mapping or a sequence starting at line i with this indentation.
        is_list = lines[i].lstrip().startswith("- ") or lines[i].strip() == "-"
        container = [] if is_list else {}
        while i < len(lines):
            line = lines[i]
            ind = len(line) - len(line.lstrip())
            if ind < indent or (ind == indent and is_list != (line.lstrip().startswith("- ") or line.strip() == "-")):
                break
            body = line.strip()
            if is_list:
                body = body[1:].strip()
                if not body:
                    value, i = block(i + 1, ind + 2)
                    container.append(value)
                    continue
                if re.match(r"^[A-Za-z_][\w]*:( |$)", body):
                    # "- key: value" starts a mapping item at indent + 2.
                    lines[i] = " " * (ind + 2) + body
                    value, i = block(i, ind + 2)
                    container.append(value)
                    continue
                container.append(parse_scalar(body))
                i += 1
                continue
            key, _, rest = body.partition(":")
            rest = rest.strip()
            if rest == "" and i + 1 < len(lines):
                nxt = lines[i + 1]
                nind = len(nxt) - len(nxt.lstrip())
                if nind > ind or (nind == ind and nxt.lstrip().startswith("- ")):
                    value, i = block(i + 1, nind)
                    container[key] = value
                    continue
            container[key] = [] if rest == "[]" else parse_scalar(rest)
            i += 1
        return container, i

    if not lines:
        return {}
    root, _ = block(0, len(lines[0]) - len(lines[0].lstrip()))
    return root.get("MonoBehaviour", root) if isinstance(root, dict) else root


def find_config(project):
    for path in sorted((project / "Assets").rglob("*.asset")):
        try:
            head = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if "textureFolder:" in head and "newTexturesAsSprites:" in head and "files:" in head:
            return path
    return None


def load_config(project, guids=None):
    path = find_config(project)
    if path is None:
        return None, None
    data = parse_unity_yaml(path.read_text(encoding="utf-8", errors="replace"))
    guids = guids if guids is not None else guid_index(project)
    files = []
    for f in data.get("files") or []:
        textures = []
        for t in f.get("textures") or []:
            ref = t.get("texture") or {}
            g = ref.get("guid") if isinstance(ref, dict) else None
            textures.append({
                "layerId": t.get("layerId"), "layerName": t.get("layerName"),
                "texture": guids.get(g) if g else None, "guid": g,
                "scale": t.get("scale"), "fit": t.get("matchTextureSize") == 1,
                "lastExported": t.get("lastExported") or None,
            })
        fig = f.get("path") or ""
        fig_full = Path(fig) if os.path.isabs(fig) else project / fig
        files.append({
            "name": f.get("name"), "path": fig, "exists": bool(fig) and fig_full.exists(),
            "figmaUrl": f.get("figmaUrl") or None, "version": str(f.get("version")) if f.get("version") not in (None, "") else None,
            "lastUpdated": f.get("lastUpdated") or None, "textures": textures,
        })
    return path.relative_to(project).as_posix(), {
        "importFolder": data.get("importFolder"), "textureFolder": data.get("textureFolder"),
        "newTexturesAsSprites": data.get("newTexturesAsSprites") == 1, "files": files,
    }


# ─── References ───────────────────────────────────────────────────────────────

def user_files(project):
    for dirpath, dirnames, filenames in os.walk(project / "Assets"):
        dirnames[:] = [d for d in dirnames if not d.endswith("~") and not d.startswith(".")]
        for f in filenames:
            if Path(f).suffix in USER_EXTENSIONS:
                yield Path(dirpath) / f


def documents(text):
    """(class id, file id, body) for each object of a Unity YAML file."""
    for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- |\Z)", text, re.S | re.M):
        yield m.group(1), m.group(2), m.group(3)


def where_used(project, guids_wanted, guids):
    """guid -> [{path, objects: [{gameObject, component, property}]}] for every text asset referring to it."""
    result = {g: [] for g in guids_wanted}
    pattern = re.compile(r"guid: (" + "|".join(guids_wanted) + r")")
    script_names = {}
    config = find_config(project)
    for path in user_files(project):
        if config is not None and path.resolve() == config.resolve():
            # The Fig Editor config links the textures; it doesn't use them.
            continue
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        if not pattern.search(text):
            continue
        names = {}
        docs = list(documents(text))
        for cls, fid, body in docs:
            if cls == "1":
                m = re.search(r"^\s*m_Name: (.*)$", body, re.M)
                names[fid] = m.group(1).strip() if m else "?"
        hits = {}
        for cls, fid, body in docs:
            for line in body.splitlines():
                m = pattern.search(line)
                if not m:
                    continue
                g = m.group(1)
                go = re.search(r"m_GameObject: \{fileID: (-?\d+)\}", body)
                component = CLASS_NAMES.get(cls, "class " + cls)
                if cls == "114":
                    s = re.search(r"m_Script: \{fileID: \d+, guid: ([0-9a-f]{32})", body)
                    if s:
                        sg = s.group(1)
                        if sg not in script_names:
                            p = guids.get(sg)
                            script_names[sg] = Path(p).stem if p else KNOWN_SCRIPTS.get(sg, "MonoBehaviour")
                        component = script_names[sg]
                prop = line.strip().split(":", 1)[0].lstrip("- ")
                hits.setdefault(g, []).append({
                    "gameObject": names.get(go.group(1)) if go else None,
                    "component": component, "property": prop,
                })
        rel = path.relative_to(project).as_posix()
        for g, objects in hits.items():
            result[g].append({"path": rel, "objects": objects})
    return result


# Script GUIDs of common UI components (in package caches the .meta may not be indexed).
KNOWN_SCRIPTS = {
    "fe87c0e1cc204ed48ad3b37840f39efc": "Image",
    "1344c3c82d62a2a41a3576d8abb8e3ea": "RawImage",
    "5f7201a12d95ffc409449d95f23cf332": "Text",
    "f4688fdb7df04437aeb418b961361dc5": "TextMeshProUGUI",
}


def texture_type(path):
    meta = Path(str(path) + ".meta")
    if not meta.exists():
        return None
    m = re.search(r"^\s*textureType: (\d+)", meta.read_text(encoding="utf-8", errors="replace"), re.M)
    return {"0": "Default", "1": "NormalMap", "2": "GUI", "8": "Sprite", "10": "SingleChannel"}.get(m.group(1), m.group(1)) if m else None


def resolve_asset(project, text, guids):
    if re.fullmatch(r"[0-9a-f]{32}", text):
        return guids.get(text), text
    p = Path(text)
    rel = p.relative_to(project).as_posix() if p.is_absolute() else p.as_posix()
    return rel, meta_guid(project / rel)


# ─── Offline commands ─────────────────────────────────────────────────────────

def cmd_status(project, args):
    alive, why = bridge_alive(project)
    config_path, config = load_config(project)
    return out({"ok": True, "project": str(project), "editorBridge": alive, "editorBridgeNote": why,
                "config": config_path,
                "files": [{"name": f["name"], "path": f["path"], "exists": f["exists"], "textures": len(f["textures"])}
                          for f in (config or {}).get("files", [])]})


def cmd_config(project, args):
    config_path, config = load_config(project)
    if config is None:
        return out({"ok": True, "config": None, "note": "No Fig Editor project config in Assets/ yet."})
    return out({"ok": True, "config": config_path, **config})


def cmd_guid(project, args):
    guids = guid_index(project) if re.fullmatch(r"[0-9a-f]{32}", args.asset) else None
    path, g = resolve_asset(project, args.asset, guids or {})
    return out({"ok": bool(path and g), "path": path, "guid": g})


def cmd_where_used(project, args):
    guids = guid_index(project)
    path, g = resolve_asset(project, args.asset, guids)
    if not g:
        return out({"ok": False, "error": f"No .meta (GUID) for {args.asset}."})
    users = where_used(project, [g], guids)[g]
    return out({"ok": True, "asset": path, "guid": g, "importAs": texture_type(project / path) if path else None, "users": users})


def cmd_refs(project, args):
    guids = guid_index(project)
    config_path, config = load_config(project, guids)
    if config is None:
        return out({"ok": True, "config": None, "textures": []})
    linked = [(f, t) for f in config["files"] for t in f["textures"]]
    wanted = [t["guid"] for _, t in linked if t["guid"]]
    users = where_used(project, wanted, guids) if wanted else {}
    textures, missing = [], []
    for f, t in linked:
        if not t["texture"]:
            missing.append({"file": f["name"], "layerId": t["layerId"], "layerName": t["layerName"], "guid": t["guid"]})
            continue
        u = users.get(t["guid"], [])
        textures.append({"file": f["name"], "layerId": t["layerId"], "layerName": t["layerName"],
                         "texture": t["texture"], "guid": t["guid"], "importAs": texture_type(project / t["texture"]),
                         "users": u, "unused": not u})
    return out({"ok": True, "config": config_path, "textures": textures, "missingTextures": missing})


def cmd_check(project, args):
    guids = guid_index(project)
    config_path, config = load_config(project, guids)
    problems = []
    if config is None:
        return out({"ok": True, "config": None, "problems": ["No Fig Editor project config yet."]})
    linked = [(f, t) for f in config["files"] for t in f["textures"] if t["guid"]]
    users = where_used(project, [t["guid"] for _, t in linked], guids) if linked else {}
    for f in config["files"]:
        if not f["exists"]:
            problems.append(f"{f['name']}: the .fig file {f['path']} is missing.")
        if not f["figmaUrl"]:
            problems.append(f"{f['name']}: no Figma link (can't be updated from Figma).")
        for t in f["textures"]:
            label = f"{f['name']} / {t['layerName']} ({t['layerId']})"
            if not t["texture"]:
                problems.append(f"{label}: its texture is missing (deleted, or its .meta changed).")
                continue
            kind = texture_type(project / t["texture"])
            for user in users.get(t["guid"], []):
                for o in user["objects"]:
                    if o["component"] in ("Image", "SpriteRenderer") and kind != "Sprite":
                        problems.append(f"{label}: {t['texture']} is used as a sprite in {user['path']} but imported as {kind}.")
            if not users.get(t["guid"]):
                problems.append(f"{label}: {t['texture']} isn't referenced by any prefab, scene or material.")
            if not t["lastExported"]:
                problems.append(f"{label}: never exported (run: call exportAll).")
    return out({"ok": True, "config": config_path, "problems": problems})


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--project", default=".", help="the Unity project folder (default: found from the current folder)")
    sub = parser.add_subparsers(dest="cmd", required=True)

    call = sub.add_parser("call", help="run a command in the open Unity editor")
    call.add_argument("command", help="ping, config, list, find, render, export, exportAll, refs, scan, assign, update, import")
    call.add_argument("params", nargs="*", help="key=value (values are JSON when they parse as JSON)")
    call.add_argument("--json", help="parameters as one JSON object")
    call.add_argument("--timeout", type=float, help="seconds to wait (default 120, 900 for update/import/exportAll/refs)")
    call.add_argument("--no-wait-check", action="store_true", help=argparse.SUPPRESS)

    sub.add_parser("status", help="is the editor bridge up; which design files are configured")
    sub.add_parser("config", help="the project config (design files, Figma links, layer -> texture links)")
    g = sub.add_parser("guid", help="GUID of an asset path, or path of a GUID")
    g.add_argument("asset")
    w = sub.add_parser("where-used", help="prefabs / scenes / materials referencing an asset (path or GUID)")
    w.add_argument("asset")
    sub.add_parser("refs", help="every linked texture with its users")
    sub.add_parser("check", help="problems: missing files or textures, unused textures, sprite import mismatches")

    args = parser.parse_args()
    project = find_project(args.project)
    handlers = {"call": cmd_call, "status": cmd_status, "config": cmd_config, "guid": cmd_guid,
                "where-used": cmd_where_used, "refs": cmd_refs, "check": cmd_check}
    sys.exit(handlers[args.cmd](project, args))


if __name__ == "__main__":
    main()
