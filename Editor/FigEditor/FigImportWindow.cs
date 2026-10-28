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
            w.minSize = new Vector2(460, 220);
            w.ShowUtility();
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

                EditorGUILayout.HelpBox("Updating replaces this file with Figma's current version. Edits made here since the import are replaced; a .bak copy of the file is kept.", MessageType.None);
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

        private static string Describe(FigmaSource s)
        {
            string when = s.ImportedAt ?? "?";
            return string.IsNullOrEmpty(s.Version) ? when : when + " · version " + s.Version;
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
            task = FigmaApi.Download(target, only, FigmaApi.Token, updatePath != null && !force ? source.Version : null, (s, p) =>
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

            if (result.Warnings.Count > 0)
            {
                var sb = new StringBuilder("Fig Editor: imported " + Path.GetFileName(path) + " from Figma with notes:");
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
