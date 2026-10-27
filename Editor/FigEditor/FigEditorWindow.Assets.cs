using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>The Images tab (image members: preview, export, replace) and the File tab (meta, thumbnail, document fields).</summary>
    public partial class FigEditorWindow
    {
        private const float ThumbSize = 64f;

        private readonly Dictionary<string, Texture2D> textures = new();
        private Dictionary<string, List<string>> imageUsers;
        private string imageFocus;
        private Vector2 imagesScroll;
        private Vector2 fileScroll;
        private string metaText;
        private string metaTextSource;
        private bool showDocumentFields;

        // ─── Textures ────────────────────────────────────────────────────────────

        /// <summary>A texture for image <paramref name="hex"/>, or null when it is missing or not PNG/JPEG.</summary>
        private Texture2D ImageTexture(string hex)
        {
            if (file == null || hex == null)
            {
                return null;
            }

            if (textures.TryGetValue(hex, out Texture2D cached))
            {
                return cached;
            }

            Texture2D tex = LoadTexture(file.Member(FigFile.ImagePrefix + hex)?.Data, hex);
            textures[hex] = tex;
            return tex;
        }

        private static Texture2D LoadTexture(byte[] bytes, string name)
        {
            string ext = FigUtil.ImageExtension(bytes);
            if (ext != ".png" && ext != ".jpg")
            {
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, name = name };
            if (tex.LoadImage(bytes, false))
            {
                return tex;
            }

            DestroyImmediate(tex);
            return null;
        }

        private void ClearTextureCache()
        {
            foreach (Texture2D tex in textures.Values)
            {
                if (tex != null)
                {
                    DestroyImmediate(tex);
                }
            }

            textures.Clear();
            if (thumbnailTexture != null)
            {
                DestroyImmediate(thumbnailTexture);
            }

            thumbnailTexture = null;
            thumbnailSource = null;
        }

        /// <summary>Image hash (hex) → ids of the layers that reference it, found by walking every node change.</summary>
        private Dictionary<string, List<string>> ImageUsers()
        {
            if (imageUsers != null)
            {
                return imageUsers;
            }

            imageUsers = new Dictionary<string, List<string>>();
            var known = new HashSet<string>(file.Images.Select(e => e.Name.Substring(FigFile.ImagePrefix.Length)));
            var pending = new Stack<KiwiValue>();
            foreach (FigNode node in file.Nodes.Values)
            {
                pending.Clear();
                pending.Push(node.Change);
                var found = new HashSet<string>();
                while (pending.Count > 0)
                {
                    KiwiValue v = pending.Pop();
                    switch (v.Kind)
                    {
                        case KiwiValueKind.Bytes:
                            // Image hashes are SHA-1: 20 bytes.
                            if (v.Bytes.Length == 20)
                            {
                                string hex = FigUtil.Hex(v.Bytes);
                                if (known.Contains(hex))
                                {
                                    found.Add(hex);
                                }
                            }

                            break;
                        case KiwiValueKind.Array:
                            foreach (KiwiValue item in v.Items)
                            {
                                pending.Push(item);
                            }

                            break;
                        case KiwiValueKind.Object:
                            foreach (KiwiMember m in v.Members)
                            {
                                pending.Push(m.Value);
                            }

                            break;
                    }
                }

                foreach (string hex in found)
                {
                    if (!imageUsers.TryGetValue(hex, out List<string> list))
                    {
                        imageUsers[hex] = list = new List<string>();
                    }

                    list.Add(node.Id);
                }
            }

            return imageUsers;
        }

        // ─── Assets panel ────────────────────────────────────────────────────────

        private Vector2 assetsScroll;
        private string assetSearch = "";
        private List<FigNode> components;

        /// <summary>The Assets panel: the file's components (click to go to one), then its images.</summary>
        private void DrawAssetsPanel(Rect body)
        {
            if (components == null || rowsDirty)
            {
                components = new List<FigNode>();
                foreach (FigNode n in file.Nodes.Values)
                {
                    if (n.Type == "SYMBOL" || n.Type == "COMPONENT_SET")
                    {
                        components.Add(n);
                    }
                }

                components.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
            }

            assetsScroll = EditorGUILayout.BeginScrollView(assetsScroll);
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            assetSearch = EditorGUILayout.TextField(assetSearch, EditorStyles.toolbarSearchField);
            GUILayout.Space(10);
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Space(12);
            GUILayout.Label($"Components ({components.Count})", FigTheme.Bold);
            GUILayout.EndHorizontal();
            int shown = 0;
            foreach (FigNode c in components)
            {
                if (!string.IsNullOrEmpty(assetSearch) && c.Label.IndexOf(assetSearch, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (++shown > 500)
                {
                    break;
                }

                Rect r = GUILayoutUtility.GetRect(10, 26, GUILayout.ExpandWidth(true));
                r.x += 6;
                r.width -= 12;
                bool selected = c.Id == selectedId;
                if (Event.current.type == EventType.Repaint && (selected || r.Contains(Event.current.mousePosition)))
                {
                    EditorGUI.DrawRect(r, selected ? FigTheme.SelectedComponent : new Color(1, 1, 1, 0.04f));
                }

                FigTheme.DrawIcon(new Rect(r.x + 4, r.y + 5, 16, 16), "component", FigTheme.Component, 14);
                GUI.Label(new Rect(r.x + 26, r.y, r.width - 26, r.height), c.Label + "   ", FigTheme.RowLabel);
                FigNode page = PageOf(c);
                if (page != null && page.Id != boardPageId)
                {
                    GUI.Label(new Rect(r.xMax - 90, r.y, 88, r.height), page.Name, FigTheme.Faint);
                }

                if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
                {
                    SetBoardSelection(c.Id, false);
                    pendingZoomTo = c.Id;
                    Event.current.Use();
                }
            }

            if (components.Count == 0)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(12);
                GUILayout.Label("No components in this file.", FigTheme.Dim);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10);
            FigTheme.Separator(GUILayoutUtility.GetRect(10, 1, GUILayout.ExpandWidth(true)));
            GUILayout.Space(6);
            DrawImagesTab();
            EditorGUILayout.EndScrollView();
        }

        // ─── Images tab ──────────────────────────────────────────────────────────

        private void DrawImagesTab()
        {
            List<FigZipEntry> images = file.Images.ToList();
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label($"{images.Count} images · {FigUtil.FormatSize(images.Sum(i => (long)i.Data.Length))}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(images.Count == 0))
            {
                if (GUILayout.Button("Export All…", EditorStyles.toolbarButton))
                {
                    Defer(ExportAllImages);
                }
            }

            GUILayout.EndHorizontal();

            if (!file.IsArchive)
            {
                EditorGUILayout.HelpBox("This is a bare canvas (no zip), so it has no image members.", MessageType.Info);
                return;
            }

            Dictionary<string, List<string>> users = ImageUsers();
            imagesScroll = EditorGUILayout.BeginScrollView(imagesScroll);
            foreach (FigZipEntry entry in images)
            {
                string hex = entry.Name.Substring(FigFile.ImagePrefix.Length);
                bool focused = hex == imageFocus;
                Rect row = EditorGUILayout.BeginHorizontal(GUILayout.Height(ThumbSize + 6));
                if (focused && Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(row, FigTheme.Selected);
                }

                Rect thumb = GUILayoutUtility.GetRect(ThumbSize, ThumbSize, GUILayout.Width(ThumbSize), GUILayout.Height(ThumbSize));
                Texture2D tex = ImageTexture(hex);
                if (tex != null)
                {
                    GUI.DrawTexture(thumb, tex, ScaleMode.ScaleToFit);
                }
                else
                {
                    GUI.Label(thumb, FigUtil.ImageExtension(entry.Data).TrimStart('.').ToUpperInvariant(), EditorStyles.centeredGreyMiniLabel);
                }

                GUILayout.BeginVertical();
                GUILayout.Label(new GUIContent(hex.Substring(0, Math.Min(16, hex.Length)) + "…", hex), EditorStyles.boldLabel);
                string dims = tex != null ? $" · {tex.width}×{tex.height}" : "";
                int userCount = users.TryGetValue(hex, out List<string> ids) ? ids.Count : 0;
                GUILayout.Label($"{FigUtil.ImageExtension(entry.Data).TrimStart('.').ToUpperInvariant()} · {FigUtil.FormatSize(entry.Data.Length)}{dims} · used by {userCount} layer(s)", EditorStyles.miniLabel);

                GUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(userCount == 0))
                {
                    if (GUILayout.Button("Find Layers", EditorStyles.miniButtonLeft, GUILayout.Width(80)))
                    {
                        search = "img:" + hex;
                        rowsDirty = true;
                    }
                }

                if (GUILayout.Button("Export…", EditorStyles.miniButtonMid, GUILayout.Width(64)))
                {
                    byte[] data = entry.Data;
                    Defer(() => ExportBytes(data, hex.Substring(0, 12) + FigUtil.ImageExtension(data)));
                }

                if (GUILayout.Button("Replace…", EditorStyles.miniButtonRight, GUILayout.Width(70)))
                {
                    Defer(() => ReplaceImage(hex));
                }

                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();

                if (focused && Event.current.type == EventType.Repaint && imageFocusScroll)
                {
                    imageFocusScroll = false;
                    imagesScroll.y = Mathf.Max(0, row.y - 40);
                    Repaint();
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private bool imageFocusScroll = true;

        private void ReplaceImage(string hex)
        {
            string path = EditorUtility.OpenFilePanelWithFilters("Replace image", "", new[] { "Images", "png,jpg,jpeg,gif,webp", "All files", "*" });
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            byte[] bytes = File.ReadAllBytes(path);
            int references = file.ReplaceImage(hex, bytes);
            string newHex = FigUtil.Hex(FigUtil.Sha1(bytes));
            if (textures.TryGetValue(hex, out Texture2D old) && old != null)
            {
                DestroyImmediate(old);
            }

            textures.Remove(hex);
            textures.Remove(newHex);
            imageUsers = null;
            imageFocus = newHex;
            imageFocusScroll = true;
            InvalidateBoard();
            MarkDirty();
            Debug.Log($"[Fig Editor] Replaced image {hex} with {Path.GetFileName(path)} ({newHex}); updated {references} reference(s).");
        }

        private void ExportAllImages()
        {
            string dir = EditorUtility.SaveFolderPanel("Export images to", Path.GetDirectoryName(filePath), "");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            int count = 0;
            foreach (FigZipEntry entry in file.Images)
            {
                string hex = entry.Name.Substring(FigFile.ImagePrefix.Length);
                File.WriteAllBytes(Path.Combine(dir, hex + FigUtil.ImageExtension(entry.Data)), entry.Data);
                count++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[Fig Editor] Exported {count} image(s) to {dir}.");
        }

        // ─── File tab ────────────────────────────────────────────────────────────

        private Texture2D thumbnailTexture;
        private byte[] thumbnailSource;

        private void DrawFileTab()
        {
            fileScroll = EditorGUILayout.BeginScrollView(fileScroll);

            EditorGUILayout.LabelField("File", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(filePath, EditorStyles.miniLabel, GUILayout.Height(16));
            List<KiwiValue> changes = file.NodeChanges;
            int blobs = file.Message.Get("blobs")?.Items?.Count ?? 0;
            EditorGUILayout.LabelField("Container", file.IsArchive ? $"zip · {file.Members.Count + 1} members" : "bare canvas.fig");
            EditorGUILayout.LabelField("Header", $"{file.MagicString} · version {file.Version}");
            EditorGUILayout.LabelField("Chunks", $"schema {file.SchemaCompression} · message {file.MessageCompression}" + (file.ExtraChunks.Count > 0 ? $" · +{file.ExtraChunks.Count} kept as-is" : ""));
            EditorGUILayout.LabelField("Schema", $"{file.Schema.Defs.Count} definitions");
            EditorGUILayout.LabelField("Message", $"{changes?.Count ?? 0} node changes · {file.Nodes.Count} layers · {blobs} blobs");
            EditorGUILayout.HelpBox("Saving re-encodes the message with this file's own schema and writes both chunks deflated. A .bak copy of the original is written next to it before the first overwrite.", MessageType.None);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Export Message JSON…"))
            {
                Defer(() => ExportJson(file.Message, Path.GetFileNameWithoutExtension(filePath) + ".message"));
            }

            if (GUILayout.Button("Export Schema…"))
            {
                Defer(ExportSchemaText);
            }

            GUILayout.EndHorizontal();

            foreach (string warning in file.Warnings)
            {
                EditorGUILayout.HelpBox(warning, MessageType.Warning);
            }

            EditorGUILayout.Space(8);
            DrawThumbnailSection();
            EditorGUILayout.Space(8);
            DrawMetaSection();
            EditorGUILayout.Space(8);
            DrawOtherMembers();
            EditorGUILayout.Space(8);

            showDocumentFields = EditorGUILayout.Foldout(showDocumentFields, "Message fields (besides nodeChanges and blobs)", true, EditorStyles.foldoutHeader);
            if (showDocumentFields)
            {
                EditorGUI.indentLevel++;
                foreach (KiwiMember m in file.Message.Members.ToList())
                {
                    if (m.Field.Name == "nodeChanges" || m.Field.Name == "blobs")
                    {
                        EditorGUILayout.LabelField(m.Field.Name, m.Value.Summary());
                        continue;
                    }

                    DrawField(m.Field, m.Value, "message/" + m.Field.Name, m.Field.Name, null);
                }

                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.EndScrollView();
        }

        private void DrawThumbnailSection()
        {
            if (!file.IsArchive)
            {
                return;
            }

            EditorGUILayout.LabelField("Thumbnail", EditorStyles.boldLabel);
            FigZipEntry thumb = file.Member(FigFile.ThumbnailMember);
            if (thumb != null && thumbnailSource != thumb.Data)
            {
                if (thumbnailTexture != null)
                {
                    DestroyImmediate(thumbnailTexture);
                }

                thumbnailTexture = LoadTexture(thumb.Data, "thumbnail");
                thumbnailSource = thumb.Data;
            }

            if (thumbnailTexture != null)
            {
                float width = Mathf.Min(320, EditorGUIUtility.currentViewWidth - 40);
                float height = width * thumbnailTexture.height / Mathf.Max(1, thumbnailTexture.width);
                Rect r = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));
                GUI.DrawTexture(r, thumbnailTexture, ScaleMode.ScaleToFit);
            }
            else
            {
                GUILayout.Label(thumb == null ? "No thumbnail.png" : "thumbnail.png can't be previewed", EditorStyles.miniLabel);
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Replace (PNG)…", GUILayout.Width(120)))
            {
                Defer(ReplaceThumbnail);
            }

            using (new EditorGUI.DisabledScope(thumb == null))
            {
                if (GUILayout.Button("Export…", GUILayout.Width(80)))
                {
                    Defer(() => ExportBytes(thumb.Data, "thumbnail.png"));
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void ReplaceThumbnail()
        {
            string path = EditorUtility.OpenFilePanel("Replace thumbnail", "", "png");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            byte[] bytes = File.ReadAllBytes(path);
            if (FigUtil.ImageExtension(bytes) != ".png")
            {
                EditorUtility.DisplayDialog("Fig Editor", "The thumbnail must be a PNG.", "OK");
                return;
            }

            FigZipEntry thumb = file.Member(FigFile.ThumbnailMember);
            if (thumb != null)
            {
                thumb.Data = bytes;
            }
            else
            {
                file.Members.Add(new FigZipEntry(FigFile.ThumbnailMember, bytes));
            }

            MarkDirty();
        }

        private void DrawMetaSection()
        {
            FigZipEntry meta = file.Member(FigFile.MetaMember);
            if (meta == null)
            {
                return;
            }

            EditorGUILayout.LabelField("meta.json", EditorStyles.boldLabel);
            string source = Encoding.UTF8.GetString(meta.Data);
            if (metaTextSource != source)
            {
                metaText = source;
                metaTextSource = source;
            }

            metaText = EditorGUILayout.TextArea(metaText, GUILayout.MinHeight(60));
            GUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(metaText == source))
            {
                if (GUILayout.Button("Apply", GUILayout.Width(80)))
                {
                    meta.Data = Encoding.UTF8.GetBytes(metaText);
                    metaTextSource = null;
                    MarkDirty();
                    GUI.FocusControl(null);
                }

                if (GUILayout.Button("Revert", GUILayout.Width(80)))
                {
                    metaTextSource = null;
                    GUI.FocusControl(null);
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("file_name is the name Figma shows for the imported file.", MessageType.None);
        }

        private void DrawOtherMembers()
        {
            var others = file.Members.Where(m => m.Name != FigFile.MetaMember && m.Name != FigFile.ThumbnailMember && !m.Name.StartsWith(FigFile.ImagePrefix, StringComparison.Ordinal)).ToList();
            if (others.Count == 0)
            {
                return;
            }

            EditorGUILayout.LabelField("Other members", EditorStyles.boldLabel);
            foreach (FigZipEntry member in others)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(member.Name);
                GUILayout.FlexibleSpace();
                GUILayout.Label(FigUtil.FormatSize(member.Data.Length), EditorStyles.miniLabel);
                if (GUILayout.Button("Export…", EditorStyles.miniButton, GUILayout.Width(64)))
                {
                    FigZipEntry m = member;
                    Defer(() => ExportBytes(m.Data, Path.GetFileName(m.Name)));
                }

                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// The schema as text, one definition per line: <c>enum Name A=0 …</c>,
        /// <c>struct Name type field, …</c>, <c>message Name type field=id, …</c>.
        /// </summary>
        private void ExportSchemaText()
        {
            string path = EditorUtility.SaveFilePanel("Export schema", Path.GetDirectoryName(filePath), Path.GetFileNameWithoutExtension(filePath) + ".schema", "txt");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var sb = new StringBuilder();
            foreach (KiwiDef def in file.Schema.Defs)
            {
                sb.Append(def.Kind.ToString().ToLowerInvariant()).Append(' ').Append(def.Name);
                if (def.Kind == KiwiDefKind.Enum)
                {
                    foreach (KiwiField f in def.Fields)
                    {
                        sb.Append(' ').Append(f.Name).Append('=').Append(f.Value);
                    }
                }
                else
                {
                    for (int i = 0; i < def.Fields.Count; i++)
                    {
                        KiwiField f = def.Fields[i];
                        sb.Append(i == 0 ? " " : ", ").Append(f.TypeName).Append(' ').Append(f.Name);
                        if (def.Kind == KiwiDefKind.Message)
                        {
                            sb.Append('=').Append(f.Value);
                        }
                    }
                }

                sb.Append('\n');
            }

            File.WriteAllText(path, sb.ToString());
            RefreshIfInProject(path);
        }
    }
}
