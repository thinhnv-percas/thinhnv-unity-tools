using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// The project's Fig Editor settings, shared through version control: the design files the game
    /// uses (where the .fig is, the Figma link it was imported from and its version) and, per file,
    /// which layers are exported into which game textures. One per project; the first one found is
    /// used.
    /// </summary>
    [CreateAssetMenu(fileName = "FigEditorConfig", menuName = "Thinhnv/Fig Editor Config", order = 400)]
    public sealed class FigProjectConfig : ScriptableObject
    {
        public const string DefaultPath = "Assets/FigEditor/FigEditorConfig.asset";

        [Serializable]
        public sealed class TextureTarget
        {
            [Tooltip("Figma layer id (\"12:34\"); stays the same when the file is updated from Figma.")]
            public string layerId;

            [Tooltip("The layer's name when it was linked; used to find it again if its id changed.")]
            public string layerName;

            public Texture2D texture;

            [Tooltip("Export scale (1 = Figma pixels).")]
            public float scale = 1f;

            [Tooltip("Render at the texture's current width instead of the scale.")]
            public bool matchTextureSize;

            [HideInInspector] public string lastHash;
            [HideInInspector] public string lastExported;
        }

        [Serializable]
        public sealed class DesignFile
        {
            public string name;

            [Tooltip("Project-relative (Assets/…) or absolute path of the .fig file.")]
            public string path;

            [Tooltip("The Figma link the file is imported / updated from.")]
            public string figmaUrl;

            [HideInInspector] public string fileKey;
            [HideInInspector] public string nodeIds;
            [HideInInspector] public string version;
            [HideInInspector] public string lastUpdated;

            [Tooltip("After an update from Figma, export this file's textures again.")]
            public bool exportTexturesAfterUpdate = true;

            public List<TextureTarget> textures = new();

            public TextureTarget Find(string layerId)
            {
                return textures.Find(t => t.layerId == layerId);
            }
        }

        [Tooltip("Where files imported from Figma links are written.")]
        public string importFolder = "Assets/Figma";

        [Tooltip("Where \"New texture…\" creates textures.")]
        public string textureFolder = "Assets/Textures/Figma";

        [Tooltip("New textures are imported as sprites (UI); otherwise as default textures.")]
        public bool newTexturesAsSprites = true;

        public List<DesignFile> files = new();

        // ─── Finding the project's config ────────────────────────────────────────

        private static FigProjectConfig cached;

        /// <summary>The project's config, or null when there is none.</summary>
        public static FigProjectConfig Instance
        {
            get
            {
                if (cached != null)
                {
                    return cached;
                }

                foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(FigProjectConfig)))
                {
                    cached = AssetDatabase.LoadAssetAtPath<FigProjectConfig>(AssetDatabase.GUIDToAssetPath(guid));
                    if (cached != null)
                    {
                        break;
                    }
                }

                return cached;
            }
        }

        /// <summary>The project's config, created at <see cref="DefaultPath"/> when there is none.</summary>
        public static FigProjectConfig GetOrCreate()
        {
            FigProjectConfig config = Instance;
            if (config != null)
            {
                return config;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath) ?? "Assets");
            config = CreateInstance<FigProjectConfig>();
            config.importFolder = FigmaApi.ImportFolder;
            AssetDatabase.CreateAsset(config, DefaultPath);
            AssetDatabase.SaveAssets();
            cached = config;
            Debug.Log("Fig Editor: created the project config at " + DefaultPath + ".", config);
            return config;
        }

        private void OnEnable()
        {
            if (cached == null)
            {
                cached = this;
            }
        }

        // ─── Files ───────────────────────────────────────────────────────────────

        /// <summary>A path as stored: project-relative inside the project, else absolute.</summary>
        public static string StoredPath(string path)
        {
            return FigImportWindow.ProjectRelative(path);
        }

        public DesignFile FindFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string full = Path.GetFullPath(path);
            return files.Find(f => !string.IsNullOrEmpty(f.path) && string.Equals(Path.GetFullPath(f.path), full, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The entry for a .fig file, added when missing (recorded for undo).</summary>
        public DesignFile AddFile(string path, FigmaSource source)
        {
            DesignFile entry = FindFile(path);
            Undo.RecordObject(this, entry == null ? "Add Design File" : "Update Design File");
            if (entry == null)
            {
                entry = new DesignFile { name = Path.GetFileNameWithoutExtension(path), path = StoredPath(path) };
                files.Add(entry);
            }

            if (source != null)
            {
                entry.figmaUrl = source.Url ?? entry.figmaUrl;
                entry.fileKey = source.FileKey;
                entry.nodeIds = source.NodeIds;
                entry.version = source.Version;
                entry.lastUpdated = source.ImportedAt ?? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm");
                if (!string.IsNullOrEmpty(source.Name))
                {
                    entry.name = source.Name;
                }
            }

            Save();
            return entry;
        }

        /// <summary>Link a layer to a texture (replacing its previous target).</summary>
        public TextureTarget SetTarget(string path, string layerId, string layerName, Texture2D texture, float scale)
        {
            DesignFile file = FindFile(path) ?? AddFile(path, null);
            Undo.RecordObject(this, "Set Export Texture");
            TextureTarget target = file.Find(layerId);
            if (texture == null)
            {
                if (target != null)
                {
                    file.textures.Remove(target);
                }

                Save();
                return null;
            }

            if (target == null)
            {
                target = new TextureTarget { layerId = layerId, scale = scale };
                file.textures.Add(target);
            }

            target.layerName = layerName;
            if (target.texture != texture)
            {
                target.texture = texture;
                target.lastHash = null;
            }

            Save();
            return target;
        }

        public void Save()
        {
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssetIfDirty(this);
        }

        /// <summary>A file was imported or updated from Figma: record it, and refresh its textures when asked to.</summary>
        public static void OnFileImported(string path, FigmaSource source, bool updated)
        {
            FigProjectConfig config = Instance;
            if (config == null)
            {
                return;
            }

            DesignFile entry = config.AddFile(path, source);
            if (updated && entry.exportTexturesAfterUpdate && entry.textures.Count > 0)
            {
                FigTextureExport.ExportFile(config, entry, false);
            }
        }
    }
}
