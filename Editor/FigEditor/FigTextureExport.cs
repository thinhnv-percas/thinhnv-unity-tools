using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Renders layers of a .fig file into the game's textures. An export overwrites the texture's
    /// file in place (PNG, JPG or TGA, by its extension), so the texture keeps its GUID, its import
    /// settings and every reference to it; an export whose pixels didn't change isn't written.
    /// </summary>
    public static class FigTextureExport
    {
        public struct Outcome
        {
            public bool Ok;
            public bool Written;
            public string Message;
        }

        /// <summary>
        /// Render layer <paramref name="layerId"/> (with its subtree and effects, on transparent) at
        /// <paramref name="scale"/>, rows bottom-up as textures want them.
        /// </summary>
        public static Color32[] Render(FigFile file, FigScene scene, string layerId, float scale, out int width, out int height)
        {
            width = height = 0;
            FigSceneNode n = scene?.Find(layerId);
            if (n == null)
            {
                return null;
            }

            Rect b = n.SubtreeBounds;
            int x = Mathf.FloorToInt(b.xMin * scale), y = Mathf.FloorToInt(b.yMin * scale);
            width = Mathf.Clamp(Mathf.CeilToInt(b.xMax * scale) - x, 1, 8192);
            height = Mathf.Clamp(Mathf.CeilToInt(b.yMax * scale) - y, 1, 8192);
            var renderer = new FigRenderer();
            var images = new Dictionary<string, FigImageData>();
            renderer.Images = hash =>
            {
                if (!images.TryGetValue(hash, out FigImageData data))
                {
                    data = Decode(file.Member(FigFile.ImagePrefix + hash)?.Data);
                    images[hash] = data;
                }

                return data;
            };

            Color32[] topDown = renderer.RenderOnly(scene.Nodes, new[] { n.Index }, scale, x, y, width, height);
            var pixels = new Color32[topDown.Length];
            for (int row = 0; row < height; row++)
            {
                Array.Copy(topDown, row * width, pixels, (height - 1 - row) * width, width);
            }

            return pixels;
        }

        /// <summary>The image bytes for <paramref name="path"/>'s format (by extension), or null for formats that can't be written.</summary>
        public static byte[] Encode(Color32[] pixels, int width, int height, string path)
        {
            var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply();
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".png":
                        return tex.EncodeToPNG();
                    case ".jpg":
                    case ".jpeg":
                        return tex.EncodeToJPG(95);
                    case ".tga":
                        return tex.EncodeToTGA();
                    default:
                        return null;
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }

        /// <summary>Export one target of a file whose scene is built (from the open editor, or loaded).</summary>
        public static Outcome Export(FigFile file, FigScene scene, FigProjectConfig.TextureTarget target, bool force)
        {
            string assetPath = target.texture != null ? AssetDatabase.GetAssetPath(target.texture) : null;
            if (string.IsNullOrEmpty(assetPath))
            {
                return new Outcome { Message = $"{target.layerName}: the target texture is missing." };
            }

            FigSceneNode layer = scene?.Find(target.layerId) ?? FindByName(scene, target.layerName);
            if (layer == null)
            {
                return new Outcome { Message = $"{target.layerName}: no layer {target.layerId} on this page of the file." };
            }

            if (layer.Id != target.layerId)
            {
                // Found by name: remember the new id.
                target.layerId = layer.Id;
            }

            float scale = target.scale > 0 ? target.scale : 1f;
            if (target.matchTextureSize && target.texture.width > 0 && layer.SubtreeBounds.width > 0)
            {
                scale = target.texture.width / layer.SubtreeBounds.width;
            }

            Color32[] pixels = Render(file, scene, layer.Id, scale, out int w, out int h);
            if (pixels == null)
            {
                return new Outcome { Message = $"{target.layerName}: nothing to render." };
            }

            string hash = Hash(pixels, w, h);
            if (!force && hash == target.lastHash && File.Exists(assetPath))
            {
                return new Outcome { Ok = true, Message = $"{target.layerName}: unchanged." };
            }

            byte[] bytes = Encode(pixels, w, h, assetPath);
            if (bytes == null)
            {
                return new Outcome { Message = $"{target.layerName}: {Path.GetExtension(assetPath)} textures can't be written; use PNG, JPG or TGA." };
            }

            File.WriteAllBytes(assetPath, bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            target.lastHash = hash;
            target.lastExported = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            return new Outcome { Ok = true, Written = true, Message = $"{target.layerName} → {assetPath} ({w}×{h})" };
        }

        /// <summary>Create a texture asset for a layer (PNG, imported as a sprite or a default texture per the config).</summary>
        public static Texture2D CreateTexture(FigFile file, FigScene scene, string layerId, float scale, string assetPath, bool asSprite)
        {
            Color32[] pixels = Render(file, scene, layerId, scale, out int w, out int h);
            if (pixels == null)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(assetPath) ?? "Assets");
            File.WriteAllBytes(assetPath, Encode(pixels, w, h, ".png"));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(assetPath) is TextureImporter importer)
            {
                importer.textureType = asSprite ? TextureImporterType.Sprite : TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        }

        /// <summary>Export every texture of a design file (loading it from disk); logs a summary.</summary>
        public static int ExportFile(FigProjectConfig config, FigProjectConfig.DesignFile entry, bool force)
        {
            if (entry.textures.Count == 0)
            {
                return 0;
            }

            FigFile file;
            try
            {
                file = FigFile.Load(entry.path);
            }
            catch (Exception e)
            {
                Debug.LogError($"Fig Editor: couldn't read {entry.path}: {e.Message}", config);
                return 0;
            }

            return ExportTargets(config, file, entry, entry.textures, force);
        }

        /// <summary>Export some targets of a loaded file (one scene per page they're on).</summary>
        public static int ExportTargets(FigProjectConfig config, FigFile file, FigProjectConfig.DesignFile entry, IList<FigProjectConfig.TextureTarget> targets, bool force)
        {
            var scenes = new Dictionary<string, FigScene>();
            var log = new StringBuilder();
            int written = 0, failed = 0;
            Undo.RecordObject(config, "Export Textures");
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < targets.Count; i++)
                {
                    FigProjectConfig.TextureTarget t = targets[i];
                    EditorUtility.DisplayProgressBar("Fig Editor", "Exporting " + t.layerName + "…", (i + 0.5f) / targets.Count);
                    FigNode node = file.Find(t.layerId) ?? FindNodeByName(file, t.layerName);
                    FigNode page = PageOf(node);
                    FigScene scene = null;
                    if (page != null && !scenes.TryGetValue(page.Id, out scene))
                    {
                        scene = FigScene.Build(file, page);
                        scenes[page.Id] = scene;
                    }

                    Outcome o = Export(file, scene, t, force);
                    written += o.Written ? 1 : 0;
                    failed += o.Ok ? 0 : 1;
                    log.Append("\n• ").Append(o.Message);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                config.Save();
            }

            string summary = $"Fig Editor: {entry.name}: {written} texture(s) written, {targets.Count - written - failed} unchanged, {failed} failed.";
            if (failed > 0)
            {
                Debug.LogWarning(summary + log, config);
            }
            else
            {
                Debug.Log(summary + log, config);
            }

            return written;
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

        private static FigNode FindNodeByName(FigFile file, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (FigNode n in file.Nodes.Values)
            {
                if (n.Name == name && n.Type != "CANVAS" && n.Type != "DOCUMENT")
                {
                    return n;
                }
            }

            return null;
        }

        private static FigSceneNode FindByName(FigScene scene, string name)
        {
            if (scene == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (FigSceneNode n in scene.Nodes)
            {
                if (!n.Expanded && n.Name == name)
                {
                    return n;
                }
            }

            return null;
        }

        /// <summary>FNV-1a over the pixels and size: did the export change?</summary>
        private static string Hash(Color32[] pixels, int w, int h)
        {
            const ulong prime = 1099511628211UL;
            ulong hash = (14695981039346656037UL ^ (uint)w) * prime;
            hash = (hash ^ (uint)h) * prime;
            foreach (Color32 c in pixels)
            {
                hash = (hash ^ (uint)(c.r | c.g << 8 | c.b << 16 | c.a << 24)) * prime;
            }

            return hash.ToString("x16");
        }

        public static FigImageData Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(bytes, false))
                {
                    return null;
                }

                Color32[] bottomUp = tex.GetPixels32();
                var topDown = new Color32[bottomUp.Length];
                for (int y = 0; y < tex.height; y++)
                {
                    Array.Copy(bottomUp, (tex.height - 1 - y) * tex.width, topDown, y * tex.width, tex.width);
                }

                return new FigImageData(tex.width, tex.height, topDown);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
