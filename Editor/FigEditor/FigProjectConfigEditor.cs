using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>The project config's inspector: design files, their Figma links and their texture exports.</summary>
    [CustomEditor(typeof(FigProjectConfig))]
    public sealed class FigProjectConfigEditor : Editor
    {
        private readonly System.Collections.Generic.HashSet<int> folded = new();

        public override void OnInspectorGUI()
        {
            var config = (FigProjectConfig)target;
            EditorGUILayout.HelpBox(
                "The design files this project uses, the Figma links they come from, and which layers are exported into which textures. " +
                "Commit this asset so the whole team shares it. The Figma access token is per user (Preferences › Thinhnv › Fig Editor).",
                MessageType.None);

            EditorGUI.BeginChangeCheck();
            string importFolder = EditorGUILayout.TextField(new GUIContent("Import folder", "Where files imported from Figma links are written."), config.importFolder);
            string textureFolder = EditorGUILayout.TextField(new GUIContent("Texture folder", "Where \"New texture…\" creates textures."), config.textureFolder);
            bool sprites = EditorGUILayout.Toggle(new GUIContent("New textures as sprites", "Import new textures as sprites (UI) instead of default textures."), config.newTexturesAsSprites);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(config, "Fig Editor Config");
                config.importFolder = importFolder;
                config.textureFolder = textureFolder;
                config.newTexturesAsSprites = sprites;
                FigmaApi.ImportFolder = importFolder;
                config.Save();
            }

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add .fig file…"))
                {
                    string picked = EditorUtility.OpenFilePanel("Add a design file", Application.dataPath, "fig");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        FigmaSource source = null;
                        try
                        {
                            source = FigmaSource.FromFile(FigFile.Load(picked));
                        }
                        catch (System.Exception)
                        {
                            // Not readable now: added without its link.
                        }

                        config.AddFile(picked, source);
                    }
                }

                if (GUILayout.Button("Import from Figma link…"))
                {
                    FigImportWindow.ShowImport(null);
                }

                using (new EditorGUI.DisabledScope(config.files.Count == 0))
                {
                    if (GUILayout.Button("Export all textures"))
                    {
                        foreach (FigProjectConfig.DesignFile f in config.files.ToArray())
                        {
                            FigTextureExport.ExportFile(config, f, false);
                        }
                    }
                }
            }

            for (int i = 0; i < config.files.Count; i++)
            {
                if (DrawFile(config, config.files[i], i))
                {
                    GUIUtility.ExitGUI();
                }
            }
        }

        /// <summary>One design file; true when the list changed.</summary>
        private bool DrawFile(FigProjectConfig config, FigProjectConfig.DesignFile f, int index)
        {
            EditorGUILayout.Space(6);
            using var box = new EditorGUILayout.VerticalScope(EditorStyles.helpBox);
            bool open = !folded.Contains(index);
            bool exists = !string.IsNullOrEmpty(f.path) && File.Exists(f.path);
            string title = (string.IsNullOrEmpty(f.name) ? Path.GetFileNameWithoutExtension(f.path) : f.name) + (exists ? "" : "  (file missing)");
            bool nowOpen = EditorGUILayout.Foldout(open, title + "   ·   " + f.textures.Count + " texture(s)", true, EditorStyles.foldoutHeader);
            if (nowOpen != open)
            {
                if (nowOpen)
                {
                    folded.Remove(index);
                }
                else
                {
                    folded.Add(index);
                }
            }

            if (!nowOpen)
            {
                return false;
            }

            EditorGUI.BeginChangeCheck();
            string name = EditorGUILayout.TextField("Name", f.name);
            string url = EditorGUILayout.TextField(new GUIContent("Figma link", "The link this file is imported / updated from."), f.figmaUrl);
            bool reexport = EditorGUILayout.Toggle(new GUIContent("Export after update", "Export this file's textures again after updating it from Figma."), f.exportTexturesAfterUpdate);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("File", f.path);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    string picked = EditorUtility.SaveFilePanel("Design file location", Path.GetDirectoryName(f.path) ?? config.importFolder, Path.GetFileName(f.path) ?? "Design.fig", "fig");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        Undo.RecordObject(config, "Design File Path");
                        f.path = FigProjectConfig.StoredPath(picked);
                        config.Save();
                    }
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(config, "Design File");
                f.name = name;
                f.figmaUrl = url;
                f.exportTexturesAfterUpdate = reexport;
                if (FigmaLink.TryParse(url, out FigmaLink link, out string why))
                {
                    f.fileKey = link.FileKey;
                }

                config.Save();
            }

            if (!string.IsNullOrEmpty(f.version) || !string.IsNullOrEmpty(f.lastUpdated))
            {
                EditorGUILayout.LabelField("Figma version", (f.version ?? "?") + (string.IsNullOrEmpty(f.lastUpdated) ? "" : "   ·   updated " + f.lastUpdated), EditorStyles.miniLabel);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!exists))
                {
                    if (GUILayout.Button("Open in Fig Editor"))
                    {
                        FigEditorWindow.OpenImported(Path.GetFullPath(f.path), null, false);
                    }
                }

                bool linked = FigmaLink.TryParse(f.figmaUrl, out FigmaLink fl, out string unused);
                using (new EditorGUI.DisabledScope(!linked || string.IsNullOrEmpty(f.path)))
                {
                    if (GUILayout.Button(exists ? "Update from Figma" : "Import from Figma"))
                    {
                        var source = new FigmaSource
                        {
                            Url = f.figmaUrl,
                            FileKey = fl.FileKey,
                            NodeIds = exists ? f.nodeIds : fl.NodeIds,
                            Name = f.name,
                            Version = exists ? f.version : null,
                            ImportedAt = f.lastUpdated,
                        };
                        FigImportWindow.ShowUpdate(f.path, source);
                    }
                }

                using (new EditorGUI.DisabledScope(!exists || f.textures.Count == 0))
                {
                    if (GUILayout.Button("Export textures"))
                    {
                        FigTextureExport.ExportFile(config, f, true);
                    }
                }

                if (GUILayout.Button("Remove", GUILayout.Width(64)) &&
                    EditorUtility.DisplayDialog("Fig Editor Config", $"Remove {title} and its {f.textures.Count} texture link(s) from the config? Files and textures stay.", "Remove", "Cancel"))
                {
                    Undo.RecordObject(config, "Remove Design File");
                    config.files.Remove(f);
                    config.Save();
                    return true;
                }
            }

            if (f.textures.Count == 0)
            {
                EditorGUILayout.LabelField("No textures yet: select a layer in the Fig Editor and pick a texture in Design › Export.", EditorStyles.wordWrappedMiniLabel);
                return false;
            }

            EditorGUILayout.Space(2);
            for (int t = 0; t < f.textures.Count; t++)
            {
                FigProjectConfig.TextureTarget target = f.textures[t];
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(string.IsNullOrEmpty(target.layerName) ? target.layerId : target.layerName, "Layer " + target.layerId), GUILayout.MinWidth(80));
                    EditorGUI.BeginChangeCheck();
                    var texture = (Texture2D)EditorGUILayout.ObjectField(target.texture, typeof(Texture2D), false, GUILayout.MinWidth(100));
                    float scale = target.matchTextureSize ? target.scale : EditorGUILayout.FloatField(target.scale, GUILayout.Width(36));
                    bool match = GUILayout.Toggle(target.matchTextureSize, new GUIContent("Fit", "Render at the texture's current width"), EditorStyles.miniButton, GUILayout.Width(30));
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(config, "Export Texture");
                        target.texture = texture;
                        target.scale = Mathf.Clamp(scale, 0.01f, 16f);
                        target.matchTextureSize = match;
                        target.lastHash = null;
                        config.Save();
                    }

                    using (new EditorGUI.DisabledScope(!exists || target.texture == null))
                    {
                        if (GUILayout.Button(new GUIContent("Export", target.lastExported == null ? "Not exported yet" : "Last exported " + target.lastExported), EditorStyles.miniButtonLeft, GUILayout.Width(52)))
                        {
                            FigTextureExport.ExportTargets(config, FigFile.Load(f.path), f, new[] { target }, true);
                        }
                    }

                    using (new EditorGUI.DisabledScope(!exists))
                    {
                        if (GUILayout.Button(new GUIContent("Show", "Open the file at this layer"), EditorStyles.miniButtonMid, GUILayout.Width(44)))
                        {
                            FigEditorWindow.OpenImported(Path.GetFullPath(f.path), target.layerId, false);
                        }
                    }

                    if (GUILayout.Button(new GUIContent("−", "Unlink (the texture stays)"), EditorStyles.miniButtonRight, GUILayout.Width(22)))
                    {
                        Undo.RecordObject(config, "Unlink Texture");
                        f.textures.RemoveAt(t);
                        config.Save();
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
