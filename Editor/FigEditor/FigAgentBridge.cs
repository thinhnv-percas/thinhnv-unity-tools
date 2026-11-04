using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Lets coding agents drive the Fig Editor in the open Unity editor through files: a JSON request
    /// written to <c>Temp/FigAgent/requests/&lt;name&gt;.json</c> is run on the main thread and its JSON
    /// result written to <c>Temp/FigAgent/results/&lt;name&gt;.json</c>. <c>Tools~/fig-agent/fig_agent.py</c>
    /// wraps this; MCP for Unity can run the "Process Requests" menu item to skip the wait.
    ///
    /// Commands: ping, config, list, find, render, export, exportAll, refs, scan, assign, update, import.
    /// See <c>Tools~/fig-agent/README.md</c> for their parameters.
    /// </summary>
    [InitializeOnLoad]
    public static class FigAgentBridge
    {
        public const string Folder = "Temp/FigAgent";
        private const string EnabledPref = "Thinhnv.FigEditor.AgentBridge";
        private const double PollSeconds = 0.5;
        private const double HeartbeatSeconds = 5;

        private static double nextPoll, nextHeartbeat;
        private static readonly Dictionary<string, Task<Dictionary<string, object>>> running = new();

        static FigAgentBridge()
        {
            EditorApplication.update += Poll;
        }

        /// <summary>Whether the editor answers agent requests (Preferences › Thinhnv › Fig Editor).</summary>
        public static bool Enabled
        {
            get { return EditorPrefs.GetBool(EnabledPref, true); }
            set { EditorPrefs.SetBool(EnabledPref, value); }
        }

        private static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/');

        private static string Dir(string sub) => Path.Combine(Root, Folder, sub);

        private static void Poll()
        {
            double now = EditorApplication.timeSinceStartup;
            if (!Enabled || now < nextPoll)
            {
                return;
            }

            nextPoll = now + PollSeconds;
            if (now >= nextHeartbeat)
            {
                nextHeartbeat = now + HeartbeatSeconds;
                Heartbeat();
            }

            ProcessNow();
        }

        [MenuItem("Tools/Thinhnv/Fig Editor - Agent: Process Requests", false, 120)]
        private static void ProcessMenu()
        {
            Heartbeat();
            int n = ProcessNow();
            Debug.Log("Fig Editor agent bridge: " + n + " request(s) handled, " + running.Count + " still running.");
        }

        public const string SkillName = "unity-fig-assets";
        private const string PackageName = "com.thinhnv.unitytools";

        [MenuItem("Tools/Thinhnv/Fig Editor - Agent: Install Skill", false, 121)]
        private static void InstallSkillMenu()
        {
            string where = InstallSkill(out string error);
            if (where != null)
            {
                Debug.Log("Fig Editor: agent skill installed in " + where + " (SKILL.md and fig_agent.py). Agents working in this project can now use it.");
                EditorUtility.RevealInFinder(Path.Combine(where, "SKILL.md"));
            }
            else
            {
                Debug.LogError("Fig Editor: couldn't install the agent skill: " + error);
            }
        }

        /// <summary>
        /// Copy the agent skill (SKILL.md + fig_agent.py, from the package's <c>Tools~/fig-agent</c>) into
        /// <c>&lt;project&gt;/.claude/skills/unity-fig-assets</c>; returns that folder, or null with an error.
        /// </summary>
        public static string InstallSkill(out string error)
        {
            error = null;
            string package = null;
            UnityEditor.PackageManager.PackageInfo info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + PackageName + "/package.json");
            if (info != null)
            {
                package = info.resolvedPath;
            }

            string tools = package != null ? Path.Combine(package, "Tools~", "fig-agent") : null;
            if (tools == null || !File.Exists(Path.Combine(tools, "fig_agent.py")))
            {
                error = "the package's Tools~/fig-agent folder wasn't found (is " + PackageName + " installed as a package?).";
                return null;
            }

            string target = Path.Combine(Root, ".claude", "skills", SkillName);
            try
            {
                Directory.CreateDirectory(target);
                File.Copy(Path.Combine(tools, "skill", SkillName, "SKILL.md"), Path.Combine(target, "SKILL.md"), true);
                File.Copy(Path.Combine(tools, "fig_agent.py"), Path.Combine(target, "fig_agent.py"), true);
                File.Copy(Path.Combine(tools, "README.md"), Path.Combine(target, "REFERENCE.md"), true);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }

            return target.Replace('\\', '/');
        }

        /// <summary>Handle every waiting request and finish the ones that were running; returns how many were handled.</summary>
        public static int ProcessNow()
        {
            int handled = 0;
            foreach (KeyValuePair<string, Task<Dictionary<string, object>>> pair in running.ToList())
            {
                if (!pair.Value.IsCompleted)
                {
                    continue;
                }

                running.Remove(pair.Key);
                WriteResult(pair.Key, Finished(pair.Value));
                handled++;
            }

            string dir = Dir("requests");
            if (!Directory.Exists(dir))
            {
                return handled;
            }

            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => File.GetCreationTimeUtc(f)))
            {
                string name = Path.GetFileName(file);
                string text;
                try
                {
                    text = File.ReadAllText(file);
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Still being written: next poll.
                    continue;
                }

                Handle(name, text);
                handled++;
            }

            return handled;
        }

        private static void Heartbeat()
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Root, Folder));
                var alive = new Dictionary<string, object>
                {
                    ["time"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["unity"] = Application.unityVersion,
                    ["project"] = Root,
                    ["enabled"] = Enabled,
                };
                File.WriteAllText(Path.Combine(Root, Folder, "alive.json"), FigJson.Write(alive));
            }
            catch (Exception)
            {
                // Not essential.
            }
        }

        private static void Handle(string name, string text)
        {
            Dictionary<string, object> result;
            try
            {
                var req = FigJson.Parse(text) as Dictionary<string, object>;
                if (req == null)
                {
                    throw new AgentError("The request must be a JSON object with a \"command\".");
                }

                string command = FigJson.Str(req, "command") ?? "";
                switch (command)
                {
                    case "update":
                        running[name] = Update(req);
                        return;
                    case "import":
                        running[name] = Import(req);
                        return;
                    default:
                        result = Run(command, req);
                        break;
                }
            }
            catch (Exception e)
            {
                result = Error(e);
            }

            WriteResult(name, result);
        }

        private static Dictionary<string, object> Run(string command, Dictionary<string, object> req)
        {
            switch (command)
            {
                case "ping":
                    return Ping();
                case "config":
                    return Config();
                case "list":
                    return List(req);
                case "find":
                    return Find(req);
                case "render":
                    return Render(req);
                case "export":
                    return Export(req);
                case "exportAll":
                    return ExportAll(req);
                case "refs":
                    return Refs(req);
                case "scan":
                    return Scan(req);
                case "assign":
                    return Assign(req);
                default:
                    throw new AgentError("Unknown command \"" + command + "\". Commands: ping, config, list, find, render, export, exportAll, refs, scan, assign, update, import.");
            }
        }

        private static Dictionary<string, object> Finished(Task<Dictionary<string, object>> task)
        {
            if (task.Status == TaskStatus.RanToCompletion)
            {
                return task.Result;
            }

            return Error(task.Exception?.GetBaseException() ?? new AgentError("Cancelled."));
        }

        private static void WriteResult(string name, Dictionary<string, object> result)
        {
            string dir = Dir("results");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name), temp = path + ".tmp";
            File.WriteAllText(temp, FigJson.Write(result), Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);
        }

        private sealed class AgentError : Exception
        {
            public AgentError(string message) : base(message)
            {
            }
        }

        private static Dictionary<string, object> Error(Exception e)
        {
            if (!(e is AgentError) && !(e is FigmaException))
            {
                Debug.LogException(e);
            }

            return new Dictionary<string, object> { ["ok"] = false, ["error"] = e.Message };
        }

        private static Dictionary<string, object> Ok()
        {
            return new Dictionary<string, object> { ["ok"] = true };
        }

        // ─── Parameters ──────────────────────────────────────────────────────────

        private static string Need(Dictionary<string, object> req, string key)
        {
            string v = Param(req, key);
            if (string.IsNullOrEmpty(v))
            {
                throw new AgentError("Missing \"" + key + "\".");
            }

            return v;
        }

        /// <summary>A parameter as text (numbers and booleans too).</summary>
        private static string Param(Dictionary<string, object> req, string key)
        {
            if (!req.TryGetValue(key, out object v) || v == null)
            {
                return null;
            }

            return v is string s ? s : v is bool b ? (b ? "true" : "false") : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static float Number(Dictionary<string, object> req, string key, float fallback)
        {
            string v = Param(req, key);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        private static bool Flag(Dictionary<string, object> req, string key, bool fallback)
        {
            string v = Param(req, key);
            return v == null ? fallback : v == "true" || v == "1" || v == "yes";
        }

        /// <summary>A .fig by path (project-relative or absolute) or by its name in the project config.</summary>
        private static string FigPath(Dictionary<string, object> req)
        {
            string f = Need(req, "file");
            FigProjectConfig config = FigProjectConfig.Instance;
            FigProjectConfig.DesignFile byName = config?.files.Find(d => string.Equals(d.name, f, StringComparison.OrdinalIgnoreCase));
            string path = byName != null ? byName.path : f;
            string full = Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            if (!File.Exists(full))
            {
                throw new AgentError("No such .fig file: " + f + (config != null ? " (config files: " + string.Join(", ", config.files.Select(d => d.name)) + ")" : ""));
            }

            return full;
        }

        private static FigFile LoadFig(string path)
        {
            FigEditorWindow open = FigEditorWindow.WindowFor(path);
            if (open != null && open.HasUnsavedEdits)
            {
                Debug.LogWarning("Fig Editor agent bridge: " + Path.GetFileName(path) + " has unsaved edits in the Fig Editor; the agent reads the saved file.");
            }

            return FigFile.Load(path);
        }

        private static string Rel(string path) => FigImportWindow.ProjectRelative(path);

        // ─── Commands ────────────────────────────────────────────────────────────

        private static Dictionary<string, object> Ping()
        {
            Dictionary<string, object> r = Ok();
            r["unity"] = Application.unityVersion;
            r["project"] = Root;
            r["config"] = FigProjectConfig.Instance != null ? AssetDatabase.GetAssetPath(FigProjectConfig.Instance) : null;
            r["figmaToken"] = FigmaApi.Token.Length > 0;
            r["running"] = (double)running.Count;
            return r;
        }

        private static Dictionary<string, object> Config()
        {
            FigProjectConfig config = FigProjectConfig.Instance;
            Dictionary<string, object> r = Ok();
            if (config == null)
            {
                r["config"] = null;
                r["note"] = "No project config yet: it is created when a texture is linked (or Tools › Thinhnv › Fig Editor - Project Config).";
                return r;
            }

            r["config"] = AssetDatabase.GetAssetPath(config);
            r["importFolder"] = config.importFolder;
            r["textureFolder"] = config.textureFolder;
            r["newTexturesAsSprites"] = config.newTexturesAsSprites;
            var files = new List<object>();
            foreach (FigProjectConfig.DesignFile f in config.files)
            {
                var textures = new List<object>();
                foreach (FigProjectConfig.TextureTarget t in f.textures)
                {
                    textures.Add(TargetJson(t));
                }

                files.Add(new Dictionary<string, object>
                {
                    ["name"] = f.name,
                    ["path"] = f.path,
                    ["exists"] = !string.IsNullOrEmpty(f.path) && File.Exists(Path.IsPathRooted(f.path) ? f.path : Path.Combine(Root, f.path)),
                    ["figmaUrl"] = f.figmaUrl,
                    ["version"] = f.version,
                    ["lastUpdated"] = f.lastUpdated,
                    ["exportTexturesAfterUpdate"] = f.exportTexturesAfterUpdate,
                    ["textures"] = textures,
                });
            }

            r["files"] = files;
            return r;
        }

        private static Dictionary<string, object> TargetJson(FigProjectConfig.TextureTarget t)
        {
            string path = t.texture != null ? AssetDatabase.GetAssetPath(t.texture) : null;
            return new Dictionary<string, object>
            {
                ["layerId"] = t.layerId,
                ["layerName"] = t.layerName,
                ["texture"] = path,
                ["guid"] = path != null ? AssetDatabase.AssetPathToGUID(path) : null,
                ["scale"] = (double)t.scale,
                ["fit"] = t.matchTextureSize,
                ["lastExported"] = t.lastExported,
            };
        }

        private static Dictionary<string, object> List(Dictionary<string, object> req)
        {
            string path = FigPath(req);
            FigFile file = LoadFig(path);
            int depth = Mathf.Clamp((int)Number(req, "depth", 2), 1, 32);
            string pageFilter = Param(req, "page");
            string under = Param(req, "under");
            Dictionary<string, FigProjectConfig.TextureTarget> links = Links(path);
            var pages = new List<object>();
            foreach (FigNode page in Pages(file))
            {
                if (pageFilter != null && page.Id != pageFilter && !string.Equals(page.Name, pageFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                IEnumerable<FigNode> roots = page.Children;
                if (under != null)
                {
                    FigNode u = file.Find(under);
                    if (u == null || PageOf(u) != page)
                    {
                        continue;
                    }

                    roots = u.Children;
                }

                var layers = new List<object>();
                foreach (FigNode n in roots)
                {
                    layers.Add(LayerJson(n, depth - 1, links));
                }

                pages.Add(new Dictionary<string, object> { ["id"] = page.Id, ["name"] = page.Name, ["layers"] = layers });
            }

            Dictionary<string, object> r = Ok();
            r["file"] = Rel(path);
            r["pages"] = pages;
            return r;
        }

        private static Dictionary<string, object> LayerJson(FigNode n, int depth, Dictionary<string, FigProjectConfig.TextureTarget> links)
        {
            Rect b = Bounds(n);
            var j = new Dictionary<string, object>
            {
                ["id"] = n.Id,
                ["name"] = n.Name,
                ["type"] = n.Type,
                ["x"] = Round(b.x),
                ["y"] = Round(b.y),
                ["w"] = Round(b.width),
                ["h"] = Round(b.height),
            };

            if (n.Change.Get("visible")?.AsBool == false)
            {
                j["visible"] = false;
            }

            if (n.Type == "TEXT")
            {
                string text = n.Change.Path("textData.characters")?.String ?? "";
                j["text"] = text.Length > 120 ? text.Substring(0, 120) + "…" : text;
            }

            if (links.TryGetValue(n.Id, out FigProjectConfig.TextureTarget t))
            {
                j["texture"] = t.texture != null ? AssetDatabase.GetAssetPath(t.texture) : null;
            }

            if (n.Children.Count > 0)
            {
                if (depth > 0)
                {
                    var children = new List<object>();
                    foreach (FigNode c in n.Children)
                    {
                        children.Add(LayerJson(c, depth - 1, links));
                    }

                    j["children"] = children;
                }
                else
                {
                    j["childCount"] = (double)n.Children.Count;
                }
            }

            return j;
        }

        private static Dictionary<string, object> Find(Dictionary<string, object> req)
        {
            string path = FigPath(req);
            FigFile file = LoadFig(path);
            string query = (Param(req, "query") ?? "").ToLowerInvariant();
            string type = Param(req, "type");
            int limit = Mathf.Clamp((int)Number(req, "limit", 50), 1, 1000);
            Dictionary<string, FigProjectConfig.TextureTarget> links = Links(path);
            var found = new List<object>();
            foreach (FigNode page in Pages(file))
            {
                var stack = new Stack<FigNode>(page.Children.AsEnumerable().Reverse());
                while (stack.Count > 0 && found.Count < limit)
                {
                    FigNode n = stack.Pop();
                    for (int i = n.Children.Count - 1; i >= 0; i--)
                    {
                        stack.Push(n.Children[i]);
                    }

                    if (type != null && !string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string text = n.Type == "TEXT" ? n.Change.Path("textData.characters")?.String ?? "" : "";
                    if (query.Length > 0 && !n.Name.ToLowerInvariant().Contains(query) && !text.ToLowerInvariant().Contains(query) && n.Id != query)
                    {
                        continue;
                    }

                    Dictionary<string, object> j = LayerJson(n, 0, links);
                    j["page"] = page.Name;
                    j["path"] = PathOf(n);
                    found.Add(j);
                }
            }

            Dictionary<string, object> r = Ok();
            r["file"] = Rel(path);
            r["layers"] = found;
            r["truncated"] = found.Count >= limit;
            return r;
        }

        private static Dictionary<string, object> Render(Dictionary<string, object> req)
        {
            string path = FigPath(req);
            FigFile file = LoadFig(path);
            FigNode node = Layer(file, req);
            float scale = Mathf.Clamp(Number(req, "scale", 1f), 0.05f, 8f);
            FigScene scene = FigScene.Build(file, PageOf(node));
            Color32[] pixels = FigTextureExport.Render(file, scene, node.Id, scale, out int w, out int h);
            if (pixels == null)
            {
                throw new AgentError("Nothing to render for layer " + node.Id + ".");
            }

            string output = Param(req, "out") ?? Path.Combine(Folder, "renders", FigImportWindow.SafeName(node.Name) + "_" + node.Id.Replace(':', '-') + ".png");
            string full = Path.IsPathRooted(output) ? output : Path.Combine(Root, output);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? Root);
            File.WriteAllBytes(full, FigTextureExport.Encode(pixels, w, h, ".png"));
            Dictionary<string, object> r = Ok();
            r["layer"] = LayerJson(node, 0, Links(path));
            r["png"] = full.Replace('\\', '/');
            r["width"] = (double)w;
            r["height"] = (double)h;
            return r;
        }

        private static Dictionary<string, object> Export(Dictionary<string, object> req)
        {
            string path = FigPath(req);
            FigFile file = LoadFig(path);
            FigNode node = Layer(file, req);
            string texturePath = Need(req, "texture").Replace('\\', '/');
            if (!texturePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new AgentError("\"texture\" must be a project path under Assets/ (e.g. Assets/Textures/UI/icon.png).");
            }

            FigProjectConfig config = FigProjectConfig.GetOrCreate();
            float scale = Mathf.Clamp(Number(req, "scale", 1f), 0.01f, 16f);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            bool created = false;
            if (texture == null)
            {
                if (File.Exists(Path.Combine(Root, texturePath)))
                {
                    throw new AgentError(texturePath + " exists but isn't a texture.");
                }

                FigScene scene = FigScene.Build(file, PageOf(node));
                texture = FigTextureExport.CreateTexture(file, scene, node.Id, scale, texturePath, Flag(req, "sprite", config.newTexturesAsSprites));
                if (texture == null)
                {
                    throw new AgentError("Couldn't create " + texturePath + ".");
                }

                created = true;
            }

            FigProjectConfig.TextureTarget target = config.SetTarget(path, node.Id, node.Name, texture, scale);
            if (Param(req, "scale") != null)
            {
                // An existing link keeps its own scale unless one is given.
                target.scale = scale;
            }

            target.matchTextureSize = Flag(req, "fit", target.matchTextureSize);
            FigProjectConfig.DesignFile entry = config.FindFile(path);
            int written = created ? 1 : FigTextureExport.ExportTargets(config, file, entry, new[] { target }, true);
            config.Save();
            Dictionary<string, object> r = Ok();
            r["layer"] = LayerJson(node, 0, Links(path));
            r["texture"] = TargetJson(target);
            r["created"] = created;
            r["written"] = written > 0;
            r["width"] = (double)texture.width;
            r["height"] = (double)texture.height;
            return r;
        }

        private static Dictionary<string, object> ExportAll(Dictionary<string, object> req)
        {
            FigProjectConfig config = FigProjectConfig.Instance;
            if (config == null)
            {
                throw new AgentError("No project config: nothing is linked yet.");
            }

            string only = Param(req, "file") != null ? FigPath(req) : null;
            bool force = Flag(req, "force", false);
            var files = new List<object>();
            foreach (FigProjectConfig.DesignFile f in config.files.ToArray())
            {
                if (only != null && config.FindFile(only) != f)
                {
                    continue;
                }

                int written = FigTextureExport.ExportFile(config, f, force);
                files.Add(new Dictionary<string, object> { ["name"] = f.name, ["textures"] = (double)f.textures.Count, ["written"] = (double)written });
            }

            Dictionary<string, object> r = Ok();
            r["files"] = files;
            r["note"] = "Details per texture are in the Unity console.";
            return r;
        }

        /// <summary>Which assets use each texture (prefabs, scenes, materials, atlases… through their dependencies).</summary>
        private static Dictionary<string, object> Refs(Dictionary<string, object> req)
        {
            var wanted = new Dictionary<string, FigProjectConfig.TextureTarget>();
            var linkedFile = new Dictionary<string, string>();
            string one = Param(req, "texture");
            FigProjectConfig config = FigProjectConfig.Instance;
            if (one != null)
            {
                string p = one.StartsWith("Assets/", StringComparison.Ordinal) || one.StartsWith("Packages/", StringComparison.Ordinal) ? one : AssetDatabase.GUIDToAssetPath(one);
                if (string.IsNullOrEmpty(p) || AssetDatabase.LoadMainAssetAtPath(p) == null)
                {
                    throw new AgentError("No such asset: " + one);
                }

                wanted[p] = null;
            }

            if (config != null)
            {
                foreach (FigProjectConfig.DesignFile f in config.files)
                {
                    foreach (FigProjectConfig.TextureTarget t in f.textures)
                    {
                        string p = t.texture != null ? AssetDatabase.GetAssetPath(t.texture) : null;
                        if (p == null)
                        {
                            continue;
                        }

                        if (one == null || wanted.ContainsKey(p))
                        {
                            wanted[p] = t;
                            linkedFile[p] = f.name;
                        }
                    }
                }
            }

            Dictionary<string, List<string>> users = Users(new HashSet<string>(wanted.Keys));
            var textures = new List<object>();
            foreach (KeyValuePair<string, FigProjectConfig.TextureTarget> pair in wanted)
            {
                var list = new List<object>();
                foreach (string u in users[pair.Key])
                {
                    list.Add(new Dictionary<string, object> { ["path"] = u, ["type"] = AssetDatabase.GetMainAssetTypeAtPath(u)?.Name });
                }

                var j = new Dictionary<string, object>
                {
                    ["texture"] = pair.Key,
                    ["guid"] = AssetDatabase.AssetPathToGUID(pair.Key),
                    ["importAs"] = (AssetImporter.GetAtPath(pair.Key) as TextureImporter)?.textureType.ToString(),
                    ["users"] = list,
                    ["unused"] = list.Count == 0,
                };

                if (pair.Value != null)
                {
                    j["file"] = linkedFile[pair.Key];
                    j["layerId"] = pair.Value.layerId;
                    j["layerName"] = pair.Value.layerName;
                }

                textures.Add(j);
            }

            var missing = new List<object>();
            if (config != null)
            {
                foreach (FigProjectConfig.DesignFile f in config.files)
                {
                    foreach (FigProjectConfig.TextureTarget t in f.textures)
                    {
                        if (t.texture == null)
                        {
                            missing.Add(new Dictionary<string, object> { ["file"] = f.name, ["layerId"] = t.layerId, ["layerName"] = t.layerName });
                        }
                    }
                }
            }

            Dictionary<string, object> r = Ok();
            r["textures"] = textures;
            r["missingTextures"] = missing;
            return r;
        }

        private static readonly string[] UserExtensions = { ".prefab", ".unity", ".mat", ".asset", ".spriteatlas", ".spriteatlasv2", ".controller", ".overrideController", ".anim", ".playable", ".guiskin", ".uss", ".uxml" };

        private static Dictionary<string, List<string>> Users(HashSet<string> targets)
        {
            var users = new Dictionary<string, List<string>>();
            foreach (string t in targets)
            {
                users[t] = new List<string>();
            }

            if (targets.Count == 0)
            {
                return users;
            }

            string[] all = AssetDatabase.GetAllAssetPaths();
            for (int i = 0; i < all.Length; i++)
            {
                string p = all[i];
                if (!p.StartsWith("Assets/", StringComparison.Ordinal) || Array.IndexOf(UserExtensions, Path.GetExtension(p)) < 0 || targets.Contains(p))
                {
                    continue;
                }

                if (i % 200 == 0)
                {
                    EditorUtility.DisplayProgressBar("Fig Editor", "Finding texture users…", (float)i / all.Length);
                }

                foreach (string dep in AssetDatabase.GetDependencies(p, false))
                {
                    if (targets.Contains(dep))
                    {
                        users[dep].Add(p);
                    }
                }
            }

            EditorUtility.ClearProgressBar();
            return users;
        }

        /// <summary>Every sprite / texture reference in a prefab or the open scenes, with the linked Figma layer when there is one.</summary>
        private static Dictionary<string, object> Scan(Dictionary<string, object> req)
        {
            string asset = Param(req, "asset");
            var found = new List<object>();
            var layerByTexture = new Dictionary<string, Dictionary<string, object>>();
            FigProjectConfig config = FigProjectConfig.Instance;
            if (config != null)
            {
                foreach (FigProjectConfig.DesignFile f in config.files)
                {
                    foreach (FigProjectConfig.TextureTarget t in f.textures)
                    {
                        string p = t.texture != null ? AssetDatabase.GetAssetPath(t.texture) : null;
                        if (p != null)
                        {
                            layerByTexture[p] = new Dictionary<string, object> { ["file"] = f.name, ["layerId"] = t.layerId, ["layerName"] = t.layerName };
                        }
                    }
                }
            }

            if (asset != null)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(asset);
                try
                {
                    ScanObject(root.transform, root.name, found, layerByTexture);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
            else
            {
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    foreach (GameObject root in SceneManager.GetSceneAt(s).GetRootGameObjects())
                    {
                        ScanObject(root.transform, root.name, found, layerByTexture);
                    }
                }
            }

            Dictionary<string, object> r = Ok();
            r["asset"] = asset ?? "open scenes";
            r["references"] = found;
            return r;
        }

        private static void ScanObject(Transform t, string path, List<object> found, Dictionary<string, Dictionary<string, object>> layers)
        {
            foreach (Component c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform)
                {
                    continue;
                }

                var so = new SerializedObject(c);
                SerializedProperty it = so.GetIterator();
                while (it.NextVisible(true))
                {
                    if (it.propertyType != SerializedPropertyType.ObjectReference || !(it.objectReferenceValue is Sprite || it.objectReferenceValue is Texture))
                    {
                        continue;
                    }

                    string refPath = AssetDatabase.GetAssetPath(it.objectReferenceValue);
                    var j = new Dictionary<string, object>
                    {
                        ["object"] = path,
                        ["component"] = c.GetType().Name,
                        ["property"] = it.propertyPath,
                        ["value"] = refPath,
                        ["valueType"] = it.objectReferenceValue.GetType().Name,
                    };

                    if (refPath != null && layers.TryGetValue(refPath, out Dictionary<string, object> layer))
                    {
                        j["figma"] = layer;
                    }

                    found.Add(j);
                }
            }

            foreach (Transform child in t)
            {
                ScanObject(child, path + "/" + child.name, found, layers);
            }
        }

        /// <summary>Put a texture (as a sprite or a texture) into a component of a prefab or of the open scene.</summary>
        private static Dictionary<string, object> Assign(Dictionary<string, object> req)
        {
            string asset = Param(req, "asset");
            string objectPath = Need(req, "object");
            string texturePath = Need(req, "texture");
            string componentName = Param(req, "component");
            string property = Param(req, "property");

            GameObject prefabRoot = null;
            Transform target;
            Scene scene = default;
            if (asset != null)
            {
                prefabRoot = PrefabUtility.LoadPrefabContents(asset);
                target = FindPath(prefabRoot.transform, objectPath);
            }
            else
            {
                target = null;
                for (int s = 0; s < SceneManager.sceneCount && target == null; s++)
                {
                    scene = SceneManager.GetSceneAt(s);
                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        target = FindPath(root.transform, objectPath);
                        if (target != null)
                        {
                            break;
                        }
                    }
                }
            }

            try
            {
                if (target == null)
                {
                    throw new AgentError("No object \"" + objectPath + "\" in " + (asset ?? "the open scenes") + ". Use the scan command to see the hierarchy paths.");
                }

                Component component = null;
                foreach (Component c in target.GetComponents<Component>())
                {
                    if (c == null)
                    {
                        continue;
                    }

                    string n = c.GetType().Name;
                    if (componentName != null ? n == componentName || c.GetType().FullName == componentName : n == "Image" || n == "SpriteRenderer" || n == "RawImage")
                    {
                        component = c;
                        break;
                    }
                }

                if (component == null)
                {
                    throw new AgentError("\"" + objectPath + "\" has no " + (componentName ?? "Image, SpriteRenderer or RawImage") + " component.");
                }

                string type = component.GetType().Name;
                if (property == null)
                {
                    property = type == "RawImage" ? "m_Texture" : "m_Sprite";
                }
                var so = new SerializedObject(component);
                SerializedProperty prop = so.FindProperty(property);
                if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                {
                    throw new AgentError(type + " has no object property \"" + property + "\".");
                }

                bool wantsSprite = prop.type.Contains("Sprite");
                UnityEngine.Object value;
                if (wantsSprite)
                {
                    if (AssetImporter.GetAtPath(texturePath) is TextureImporter importer && importer.textureType != TextureImporterType.Sprite)
                    {
                        if (!Flag(req, "makeSprite", true))
                        {
                            throw new AgentError(texturePath + " isn't imported as a sprite (pass makeSprite: true to change that).");
                        }

                        importer.textureType = TextureImporterType.Sprite;
                        importer.SaveAndReimport();
                    }

                    value = AssetDatabase.LoadAllAssetsAtPath(texturePath).OfType<Sprite>().FirstOrDefault();
                }
                else
                {
                    value = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                }

                if (value == null)
                {
                    throw new AgentError("No " + (wantsSprite ? "sprite" : "texture") + " at " + texturePath + ".");
                }

                string previous = prop.objectReferenceValue != null ? AssetDatabase.GetAssetPath(prop.objectReferenceValue) : null;
                if (prefabRoot == null)
                {
                    Undo.RecordObject(component, "Assign Figma Texture");
                }

                prop.objectReferenceValue = value;
                so.ApplyModifiedProperties();
                if (Flag(req, "nativeSize", false))
                {
                    component.GetType().GetMethod("SetNativeSize", Type.EmptyTypes)?.Invoke(component, null);
                }

                if (prefabRoot != null)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, asset);
                }
                else
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    if (Flag(req, "save", false))
                    {
                        EditorSceneManager.SaveScene(scene);
                    }
                }

                Dictionary<string, object> r = Ok();
                r["asset"] = asset ?? scene.path;
                r["object"] = objectPath;
                r["component"] = type;
                r["property"] = property;
                r["previous"] = previous;
                r["value"] = texturePath;
                r["saved"] = prefabRoot != null || Flag(req, "save", false);
                return r;
            }
            finally
            {
                if (prefabRoot != null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabRoot);
                }
            }
        }

        /// <summary>"Root/Child/Leaf", with or without the root's own name first.</summary>
        private static Transform FindPath(Transform root, string path)
        {
            path = path.Trim('/');
            if (path == root.name)
            {
                return root;
            }

            if (path.StartsWith(root.name + "/", StringComparison.Ordinal))
            {
                Transform t = root.Find(path.Substring(root.name.Length + 1));
                if (t != null)
                {
                    return t;
                }
            }

            return root.Find(path);
        }

        // ─── Figma downloads ─────────────────────────────────────────────────────

        /// <summary>Update a .fig from its Figma link: scope whole | linked | frames (with "frames": "1:2,3:4").</summary>
        private static async Task<Dictionary<string, object>> Update(Dictionary<string, object> req)
        {
            string path = FigPath(req);
            FigFile local = FigFile.Load(path);
            FigmaSource source = FigmaSource.FromFile(local);
            FigProjectConfig.DesignFile entry = FigProjectConfig.Instance?.FindFile(path);
            string url = source?.Url ?? entry?.figmaUrl;
            if (!FigmaLink.TryParse(url, out FigmaLink link, out string why))
            {
                throw new AgentError("The file has no Figma link to update from" + (url != null ? " (" + why + ")" : "") + ".");
            }

            string scope = Param(req, "scope") ?? "whole";
            bool force = Flag(req, "force", false);
            var options = new FigmaDownloadOptions
            {
                Existing = File.ReadAllBytes(path),
                OnlyLinkedLayers = !string.IsNullOrEmpty(source?.NodeIds),
                SkipIfVersion = force ? null : source?.Version,
            };

            if (!string.IsNullOrEmpty(source?.NodeIds))
            {
                link.NodeIds = source.NodeIds;
            }

            if (scope == "linked" || scope == "frames")
            {
                List<string> ids = scope == "linked"
                    ? FigImportWindow.LinkedFrames(local, path)
                    : Need(req, "frames").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (ids.Count == 0)
                {
                    throw new AgentError(scope == "linked" ? "No textures are linked to this file: nothing to update with scope \"linked\"." : "\"frames\" is empty.");
                }

                options.MergeIds = ids;
                if (!force)
                {
                    var before = new HashSet<string>((source?.PartialIds ?? "").Split(','));
                    options.SkipIfVersion = before.SetEquals(ids) && !string.IsNullOrEmpty(source?.PartialVersion) ? source.PartialVersion : source?.Version;
                }
            }
            else if (scope != "whole")
            {
                throw new AgentError("\"scope\" is whole, linked or frames.");
            }

            FigmaDownload result = await FigmaApi.Download(link, options, FigmaApi.Token, null, CancellationToken.None);
            Dictionary<string, object> r = DownloadJson(result);
            if (result.UpToDate)
            {
                r["path"] = Rel(path);
                return r;
            }

            if (!FigImportWindow.WriteDownload(result, path, true, out string error))
            {
                throw new AgentError(error);
            }

            ReloadOpen(path);
            r["path"] = Rel(path);
            return r;
        }

        /// <summary>Import a Figma link as a new .fig (in "folder", default the config's import folder).</summary>
        private static async Task<Dictionary<string, object>> Import(Dictionary<string, object> req)
        {
            if (!FigmaLink.TryParse(Need(req, "url"), out FigmaLink link, out string why))
            {
                throw new AgentError(why);
            }

            bool only = Flag(req, "onlyLinked", link.NodeIds != null) && link.NodeIds != null;
            FigmaDownload result = await FigmaApi.Download(link, new FigmaDownloadOptions { OnlyLinkedLayers = only }, FigmaApi.Token, null, CancellationToken.None);
            FigProjectConfig config = FigProjectConfig.Instance;
            string folder = Param(req, "folder") ?? (config != null && !string.IsNullOrEmpty(config.importFolder) ? config.importFolder : FigmaApi.ImportFolder);
            string name = Param(req, "name") ?? FigImportWindow.SafeName(result.Name);
            string path = Path.Combine(Root, folder, name + ".fig");
            if (File.Exists(path) && !Flag(req, "replace", false))
            {
                for (int i = 2; File.Exists(path); i++)
                {
                    path = Path.Combine(Root, folder, name + " " + i + ".fig");
                }
            }

            if (!FigImportWindow.WriteDownload(result, path, false, out string error))
            {
                throw new AgentError(error);
            }

            if (config != null)
            {
                FigProjectConfig.DesignFile entry = config.FindFile(path);
                if (entry != null && string.IsNullOrEmpty(entry.figmaUrl))
                {
                    entry.figmaUrl = link.Url;
                    config.Save();
                }
            }

            ReloadOpen(path);
            Dictionary<string, object> r = DownloadJson(result);
            r["path"] = Rel(path);
            return r;
        }

        private static Dictionary<string, object> DownloadJson(FigmaDownload result)
        {
            Dictionary<string, object> r = Ok();
            r["upToDate"] = result.UpToDate;
            r["name"] = result.Name;
            r["version"] = result.Version;
            r["bytesDownloaded"] = (double)result.BytesDownloaded;
            r["imagesDownloaded"] = (double)result.ImagesDownloaded;
            r["imagesReused"] = (double)result.ImagesReused;
            r["mergedFrames"] = result.Merged != null ? result.Merged.Cast<object>().ToList() : null;
            r["warnings"] = result.Warnings.Cast<object>().ToList();
            return r;
        }

        private static void ReloadOpen(string path)
        {
            if (FigEditorWindow.WindowFor(path) != null)
            {
                FigEditorWindow.OpenImported(Path.GetFullPath(path), null, true);
            }
        }

        // ─── Layers ──────────────────────────────────────────────────────────────

        private static FigNode Layer(FigFile file, Dictionary<string, object> req)
        {
            string id = Need(req, "layer");
            FigNode node = file.Find(id);
            if (node == null)
            {
                // A unique name works too.
                List<FigNode> named = file.Nodes.Values.Where(n => n.Name == id && n.Type != "CANVAS" && n.Type != "DOCUMENT").ToList();
                if (named.Count == 1)
                {
                    node = named[0];
                }
                else
                {
                    throw new AgentError(named.Count > 1 ? $"{named.Count} layers are named \"{id}\": pass its id ({string.Join(", ", named.Take(8).Select(n => n.Id))})." : "No layer " + id + ".");
                }
            }

            if (PageOf(node) == null || node.Type == "CANVAS")
            {
                throw new AgentError("Layer " + id + " isn't a layer on a page.");
            }

            return node;
        }

        private static IEnumerable<FigNode> Pages(FigFile file)
        {
            foreach (FigNode root in file.Roots)
            {
                if (root.Type == "CANVAS")
                {
                    yield return root;
                }
                else
                {
                    foreach (FigNode c in root.Children)
                    {
                        if (c.Type == "CANVAS")
                        {
                            yield return c;
                        }
                    }
                }
            }
        }

        private static FigNode PageOf(FigNode node)
        {
            for (FigNode n = node; n != null; n = n.Parent)
            {
                if (n.Type == "CANVAS")
                {
                    return n;
                }
            }

            return null;
        }

        private static string PathOf(FigNode node)
        {
            var names = new List<string>();
            for (FigNode n = node; n != null && n.Type != "CANVAS" && n.Type != "DOCUMENT"; n = n.Parent)
            {
                names.Insert(0, n.Name);
            }

            return string.Join("/", names);
        }

        /// <summary>The layer's box on its page (axis-aligned bounds of its transformed size).</summary>
        private static Rect Bounds(FigNode node)
        {
            FigAffine world = FigAffine.Identity;
            var chain = new List<FigNode>();
            for (FigNode n = node; n != null && n.Type != "CANVAS" && n.Type != "DOCUMENT"; n = n.Parent)
            {
                chain.Insert(0, n);
            }

            foreach (FigNode n in chain)
            {
                world = world.Then(FigAffine.FromMatrix(n.Change.Get("transform")));
            }

            KiwiValue size = node.Change.Get("size");
            return FigGeometry.TransformBounds(world, new Rect(0, 0, size?.Get("x")?.AsFloat ?? 0, size?.Get("y")?.AsFloat ?? 0));
        }

        private static Dictionary<string, FigProjectConfig.TextureTarget> Links(string path)
        {
            var links = new Dictionary<string, FigProjectConfig.TextureTarget>();
            FigProjectConfig.DesignFile entry = FigProjectConfig.Instance?.FindFile(path);
            if (entry != null)
            {
                foreach (FigProjectConfig.TextureTarget t in entry.textures)
                {
                    links[t.layerId] = t;
                }
            }

            return links;
        }

        private static double Round(float v) => Math.Round(v, 2);
    }
}
