using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Import a Figma link as a <c>.fig</c> file, or update a file imported earlier from its link.
    /// Downloads run asynchronously; the editor stays usable and the download can be cancelled.
    /// </summary>
    public sealed class FigImportWindow : EditorWindow
    {
        private string link = "";
        private bool onlyLinked = true;
        private bool force;

        // Update mode: the file to replace and where it came from.
        private string updatePath;
        private FigmaSource source;

        // What an update downloads: the whole file, the frames with linked textures, or chosen frames.
        private int scope;
        private readonly System.Collections.Generic.List<Frame> frames = new();
        private readonly System.Collections.Generic.HashSet<string> chosen = new();
        private readonly System.Collections.Generic.List<string> linkedFrames = new();
        private Vector2 framesScroll;

        private struct Frame
        {
            public string Id;
            public string Label;
            public bool PageStart;
            public string Page;
        }

        private Task<FigmaDownload> task;
        private CancellationTokenSource cancel;
        private string step;
        private float progress;
        private string error;

        [MenuItem("Tools/Thinhnv/Fig Editor - Import from Figma Link", false, 101)]
        public static void ShowImport()
        {
            ShowImport(null);
        }

        public static void ShowImport(string prefill)
        {
            var w = CreateInstance<FigImportWindow>();
            w.titleContent = new GUIContent("Import from Figma");
            w.link = prefill ?? TryClipboardLink();
            w.minSize = new Vector2(460, 250);
            w.ShowUtility();
        }

        public static void ShowUpdate(string path, FigmaSource from)
        {
            var w = CreateInstance<FigImportWindow>();
            w.titleContent = new GUIContent("Update from Figma");
            w.updatePath = path;
            w.source = from;
            w.link = from.Url ?? from.FileKey;
            w.minSize = new Vector2(460, 300);
            w.ReadFrames();
            w.ShowUtility();
        }

        /// <summary>The top-level frames of the current file (to update some of them), and those with linked textures.</summary>
        private void ReadFrames()
        {
            frames.Clear();
            linkedFrames.Clear();
            if (string.IsNullOrEmpty(updatePath) || !File.Exists(updatePath))
            {
                return;
            }

            FigFile local;
            try
            {
                local = FigFile.Load(updatePath);
            }
            catch (Exception)
            {
                return;
            }

            foreach (FigNode root in local.Roots)
            {
                foreach (FigNode page in root.Type == "DOCUMENT" ? root.Children : new System.Collections.Generic.List<FigNode> { root })
                {
                    if (page.Type != "CANVAS")
                    {
                        continue;
                    }

                    bool first = true;
                    foreach (FigNode f in page.Children)
                    {
                        frames.Add(new Frame { Id = f.Id, Label = string.IsNullOrEmpty(f.Name) ? f.Type : f.Name, PageStart = first, Page = page.Name });
                        first = false;
                    }
                }
            }

            FigProjectConfig.DesignFile entry = FigProjectConfig.Instance?.FindFile(updatePath);
            if (entry != null)
            {
                foreach (FigProjectConfig.TextureTarget t in entry.textures)
                {
                    FigNode n = local.Find(t.layerId);
                    while (n?.Parent != null && n.Parent.Type != "CANVAS")
                    {
                        n = n.Parent;
                    }

                    if (n != null && n.Parent != null && !linkedFrames.Contains(n.Id))
                    {
                        linkedFrames.Add(n.Id);
                    }
                }
            }

            scope = linkedFrames.Count > 0 ? 1 : 0;
        }

        private static string TryClipboardLink()
        {
            string clip = EditorGUIUtility.systemCopyBuffer ?? "";
            return clip.Length < 500 && clip.IndexOf("figma.com/", StringComparison.OrdinalIgnoreCase) >= 0 ? clip.Trim() : "";
        }

        private void OnDisable()
        {
            cancel?.Cancel();
        }

        private void Update()
        {
            if (task == null)
            {
                return;
            }

            Repaint();
            if (task.IsCompleted)
            {
                Task<FigmaDownload> finished = task;
                task = null;
                Finish(finished);
            }
        }

        private void OnGUI()
        {
            bool updating = updatePath != null;
            EditorGUILayout.Space(8);
            if (updating)
            {
                EditorGUILayout.LabelField(Path.GetFileName(updatePath), EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Figma file", source.Name ?? source.FileKey);
                EditorGUILayout.LabelField("Imported", Describe(source));
                if (!string.IsNullOrEmpty(source.NodeIds))
                {
                    EditorGUILayout.LabelField("Layers", source.NodeIds);
                }

                DrawScope();
                EditorGUILayout.HelpBox(
                    "Figma's API has no \"changes only\" download. What is skipped: everything when Figma's version is unchanged, " +
                    "the frames not chosen, and every image this file already has. Edits made here to the updated part are replaced; a .bak copy is kept.",
                    MessageType.None);
                force = EditorGUILayout.ToggleLeft("Download even when Figma's version hasn't changed", force);
            }
            else
            {
                EditorGUILayout.LabelField("Figma link", EditorStyles.boldLabel);
                link = EditorGUILayout.TextField(link);
                bool ok = FigmaLink.TryParse(link, out FigmaLink parsed, out string why);
                if (link.Trim().Length > 0)
                {
                    EditorGUILayout.LabelField(ok ? "File " + parsed.FileKey + (parsed.NodeIds != null ? " · layer " + parsed.NodeIds : "") : why, EditorStyles.miniLabel);
                }

                if (ok && parsed.NodeIds != null)
                {
                    onlyLinked = EditorGUILayout.ToggleLeft("Only the linked layer (with its page), not the whole file", onlyLinked);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    string folder = EditorGUILayout.TextField("Save to", FigmaApi.ImportFolder);
                    if (folder != FigmaApi.ImportFolder)
                    {
                        FigmaApi.ImportFolder = folder;
                    }

                    if (GUILayout.Button("…", GUILayout.Width(24)))
                    {
                        string picked = EditorUtility.OpenFolderPanel("Import folder", FigmaApi.ImportFolder, "");
                        if (!string.IsNullOrEmpty(picked))
                        {
                            FigmaApi.ImportFolder = ProjectRelative(picked);
                        }
                    }
                }
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool hasToken = FigmaApi.Token.Length > 0;
                EditorGUILayout.LabelField("Access token", hasToken ? FigmaApi.MaskedToken + (FigmaApi.TokenFromEnvironment ? " (environment)" : "") : "not set");
                if (GUILayout.Button(hasToken ? "Change…" : "Set token…", GUILayout.Width(90)))
                {
                    FigmaSettings.Open();
                }
            }

            if (!string.IsNullOrEmpty(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            GUILayout.FlexibleSpace();
            if (task != null)
            {
                Rect bar = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(bar, progress, step ?? "Working…");
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(90)))
                    {
                        cancel?.Cancel();
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Close", GUILayout.Width(90)))
                    {
                        Close();
                        GUIUtility.ExitGUI();
                    }

                    using (new EditorGUI.DisabledScope(FigmaApi.Token.Length == 0 || (!updating && !FigmaLink.TryParse(link, out FigmaLink unusedLink, out string unusedWhy))))
                    {
                        if (GUILayout.Button(updating ? "Update" : "Import", GUILayout.Width(90)))
                        {
                            Start();
                        }
                    }
                }
            }

            EditorGUILayout.Space(8);
        }

        private void DrawScope()
        {
            bool canMerge = frames.Count > 0;
            string[] labels =
            {
                "Whole file",
                "Frames with linked textures (" + linkedFrames.Count + ")",
                "Chosen frames (" + chosen.Count + ")",
            };

            using (new EditorGUI.DisabledScope(!canMerge))
            {
                scope = GUILayout.Toolbar(canMerge ? scope : 0, labels, EditorStyles.miniButton);
            }

            if (scope == 1 && linkedFrames.Count == 0)
            {
                EditorGUILayout.LabelField("No textures are linked to this file in the project config.", EditorStyles.miniLabel);
            }

            if (scope != 2)
            {
                return;
            }

            framesScroll = EditorGUILayout.BeginScrollView(framesScroll, GUILayout.MinHeight(80), GUILayout.MaxHeight(180));
            foreach (Frame f in frames)
            {
                if (f.PageStart)
                {
                    EditorGUILayout.LabelField(f.Page, EditorStyles.miniBoldLabel);
                }

                bool on = chosen.Contains(f.Id);
                bool now = EditorGUILayout.ToggleLeft("   " + f.Label, on);
                if (now != on)
                {
                    if (now)
                    {
                        chosen.Add(f.Id);
                    }
                    else
                    {
                        chosen.Remove(f.Id);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static string Describe(FigmaSource s)
        {
            string when = s.ImportedAt ?? "?";
            string text = string.IsNullOrEmpty(s.Version) ? when : when + " · version " + s.Version;
            return string.IsNullOrEmpty(s.PartialVersion) ? text : text + " · some frames at " + s.PartialVersion;
        }

        private void Start()
        {
            error = null;
            FigmaLink target;
            bool only;
            if (updatePath != null)
            {
                target = new FigmaLink { Url = source.Url, FileKey = source.FileKey, NodeIds = source.NodeIds };
                only = !string.IsNullOrEmpty(source.NodeIds);
            }
            else if (!FigmaLink.TryParse(link, out target, out error))
            {
                return;
            }
            else
            {
                only = onlyLinked && target.NodeIds != null;
            }

            cancel = new CancellationTokenSource();
            step = "Starting…";
            progress = 0;
            var options = new FigmaDownloadOptions { OnlyLinkedLayers = only, SkipIfVersion = updatePath != null && !force ? source.Version : null };
            if (updatePath != null && File.Exists(updatePath))
            {
                options.Existing = File.ReadAllBytes(updatePath);
                System.Collections.Generic.List<string> ids = scope == 1 ? new System.Collections.Generic.List<string>(linkedFrames) :
                    scope == 2 ? new System.Collections.Generic.List<string>(chosen) : null;
                if (scope != 0 && (ids == null || ids.Count == 0))
                {
                    error = "Choose at least one frame, or update the whole file.";
                    return;
                }

                options.MergeIds = ids;
                if (ids != null && !force)
                {
                    // The same frames updated before: nothing to do while Figma's version is the one they have.
                    var before = new System.Collections.Generic.HashSet<string>((source.PartialIds ?? "").Split(','));
                    options.SkipIfVersion = before.SetEquals(ids) && !string.IsNullOrEmpty(source.PartialVersion) ? source.PartialVersion : source.Version;
                }
            }

            task = FigmaApi.Download(target, options, FigmaApi.Token, (s, p) =>
            {
                step = s;
                progress = p;
            }, cancel.Token);
            pendingNode = only ? FirstNode(target.NodeIds) : target.NodeIds != null ? FirstNode(target.NodeIds) : null;
        }

        private string pendingNode;

        private static string FirstNode(string ids)
        {
            if (string.IsNullOrEmpty(ids))
            {
                return null;
            }

            int comma = ids.IndexOf(',');
            return comma < 0 ? ids : ids.Substring(0, comma);
        }

        private void Finish(Task<FigmaDownload> finished)
        {
            if (finished.IsCanceled)
            {
                error = "Cancelled.";
                return;
            }

            if (finished.IsFaulted)
            {
                Exception e = finished.Exception?.GetBaseException();
                error = e is FigmaException || e is OperationCanceledException ? e.Message : "The download failed: " + e?.Message;
                if (!(e is FigmaException) && !(e is OperationCanceledException))
                {
                    Debug.LogException(e);
                }

                return;
            }

            FigmaDownload result = finished.Result;
            if (result.UpToDate)
            {
                EditorUtility.DisplayDialog("Update from Figma", $"{Path.GetFileName(updatePath)} is up to date (Figma version {result.Version}).", "OK");
                Close();
                return;
            }

            string path = updatePath ?? Target(result.Name);
            if (path == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }

                File.WriteAllBytes(path, result.Fig);
            }
            catch (Exception e)
            {
                error = "Couldn't write " + path + ": " + e.Message;
                return;
            }

            if (IsInProject(path))
            {
                AssetDatabase.ImportAsset(ProjectRelative(path));
            }

            string what = result.Merged != null ? $"updated {result.Merged.Count} frame(s) of " : updatePath != null ? "updated " : "imported ";
            Debug.Log($"Fig Editor: {what}{Path.GetFileName(path)} from Figma (version {result.Version}): {FigUtil.FormatSize(result.BytesDownloaded)} downloaded, " +
                      $"{result.ImagesDownloaded} image(s) downloaded, {result.ImagesReused} reused from the file.");
            if (result.Warnings.Count > 0)
            {
                var sb = new StringBuilder("Fig Editor: " + what + Path.GetFileName(path) + " from Figma with notes:");
                foreach (string w in result.Warnings)
                {
                    sb.Append("\n• ").Append(w);
                }

                Debug.Log(sb.ToString());
            }

            // The project config records the file (link, version) and refreshes its textures after an update.
            try
            {
                FigProjectConfig.OnFileImported(path, FigmaSource.FromFile(FigFile.Parse(result.Fig)), updatePath != null);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            FigEditorWindow.OpenImported(Path.GetFullPath(path), pendingNode, updatePath != null);
            Close();
        }

        /// <summary>Where a new import goes: the import folder, named after the file; asks before replacing.</summary>
        private string Target(string name)
        {
            string configured = FigProjectConfig.Instance != null ? FigProjectConfig.Instance.importFolder : null;
            string folder = !string.IsNullOrEmpty(configured) ? configured : string.IsNullOrEmpty(FigmaApi.ImportFolder) ? "Assets/Figma" : FigmaApi.ImportFolder;
            string file = SafeName(name) + ".fig";
            string path = Path.Combine(folder, file);
            if (!File.Exists(path))
            {
                return path;
            }

            int choice = EditorUtility.DisplayDialogComplex("Import from Figma", $"{path} already exists.", "Replace", "Cancel", "Keep both");
            if (choice == 1)
            {
                error = "Cancelled: the file already exists.";
                return null;
            }

            if (choice == 0)
            {
                return path;
            }

            for (int i = 2; ; i++)
            {
                string candidate = Path.Combine(folder, SafeName(name) + " " + i + ".fig");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        private static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in string.IsNullOrWhiteSpace(name) ? "Figma file" : name.Trim())
            {
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == '/' || c == '\\' || c == ':' ? '_' : c);
            }

            return sb.ToString();
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/";

        private static bool IsInProject(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/').StartsWith(ProjectRoot + "Assets/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A path inside the project as "Assets/…"; others unchanged.</summary>
        public static string ProjectRelative(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            return full.StartsWith(ProjectRoot, StringComparison.OrdinalIgnoreCase) ? full.Substring(ProjectRoot.Length) : full;
        }
    }
}
