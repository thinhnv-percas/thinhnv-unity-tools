using System.Collections.Generic;
using DreamCode.SmartImporter.Editor.IO;
using UnityEditor;
using UnityEngine;

namespace DreamCode.SmartImporter.Editor.UI
{
    /// <summary>
    /// Self-contained "Import Package" picker: lists every asset in a .unitypackage as a folder
    /// tree with per-type icons and New/Replace/Move tags, remapped under a chosen destination
    /// folder, and writes the selected assets' real bytes on Import. Replaces reliance on Unity's
    /// native PackageImport dialog, whose own file-copy step no longer works for an externally-built
    /// item list in Unity 6000.3.21f1 (verified: ShowImportPackage/PackageUtility.ImportPackageAssets
    /// complete without error but write nothing).
    /// </summary>
    internal sealed class SmartPackageImportWindow : EditorWindow
    {
        private sealed class ImportRow
        {
            internal PackageEntry Entry;
            internal string DestinationPath;
            internal string ExistingAssetPath;
            internal bool Exists;
            internal bool Enabled;
        }

        private sealed class TreeNode
        {
            internal string Name;
            internal ImportRow Row;
            internal readonly List<TreeNode> Children = new List<TreeNode>();
            internal bool Expanded = true;
        }

        private static readonly Dictionary<string, string> ExtensionIconNames =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                { ".cs", "cs Script Icon" },
                { ".mat", "Material Icon" },
                { ".prefab", "Prefab Icon" },
                { ".shader", "Shader Icon" },
                { ".shadergraph", "Shader Icon" },
                { ".asset", "ScriptableObject Icon" },
                { ".anim", "AnimationClip Icon" },
                { ".controller", "AnimatorController Icon" },
                { ".overrideController", "AnimatorOverrideController Icon" },
                { ".unity", "SceneAsset Icon" },
                { ".fbx", "Mesh Icon" },
                { ".obj", "Mesh Icon" },
                { ".png", "Texture Icon" },
                { ".jpg", "Texture Icon" },
                { ".jpeg", "Texture Icon" },
                { ".tga", "Texture Icon" },
                { ".psd", "Texture Icon" },
                { ".exr", "Texture Icon" },
                { ".wav", "AudioClip Icon" },
                { ".mp3", "AudioClip Icon" },
                { ".ogg", "AudioClip Icon" },
                { ".ttf", "Font Icon" },
                { ".otf", "Font Icon" },
                { ".physicmaterial", "PhysicMaterial Icon" },
                { ".guiskin", "GUISkin Icon" },
                { ".mask", "AvatarMask Icon" },
                { ".playable", "PlayableAsset Icon" },
                { ".mixer", "AudioMixerController Icon" },
            };

        private string _packagePath;
        private string _selectedFolder;
        private List<ImportRow> _rows;
        private List<TreeNode> _roots;
        private Vector2 _scrollPosition;
        private GUIStyle _tagStyle;
        private GUIContent _folderIcon;
        private GUIContent _defaultAssetIcon;
        private GUIContent _expandedArrowIcon;
        private GUIContent _collapsedArrowIcon;

        internal static void Open(string selectedFolder, string packagePath)
        {
            var entries = UnityPackageArchive.ReadEntries(packagePath);
            if (entries.Count == 0)
            {
                EditorUtility.DisplayDialog("Import Package", "No importable assets found in this package.", "Ok");
                return;
            }

            var rows = new List<ImportRow>(entries.Count);
            foreach (var entry in entries)
            {
                var destinationPath = selectedFolder + PathWithoutRoot(entry.ExportedAssetPath.Replace('\\', '/'));
                var existingAssetPath = AssetDatabase.GUIDToAssetPath(entry.Guid);
                rows.Add(new ImportRow
                {
                    Entry = entry,
                    DestinationPath = destinationPath,
                    ExistingAssetPath = existingAssetPath,
                    Exists = !string.IsNullOrEmpty(existingAssetPath),
                    Enabled = true
                });
            }
            rows.Sort((a, b) => string.Compare(a.DestinationPath, b.DestinationPath, System.StringComparison.OrdinalIgnoreCase));

            var window = CreateInstance<SmartPackageImportWindow>();
            window.titleContent = new GUIContent("Import Package");
            window._packagePath = packagePath;
            window._selectedFolder = selectedFolder;
            window._rows = rows;
            window._roots = BuildTree(rows);
            window.minSize = new Vector2(460, 360);
            window.ShowUtility();
        }

        private static string PathWithoutRoot(string exportedAssetPath)
        {
            var slashIndex = exportedAssetPath.IndexOf('/');
            return slashIndex >= 0 ? exportedAssetPath.Substring(slashIndex) : "/" + exportedAssetPath;
        }

        private static List<TreeNode> BuildTree(List<ImportRow> rows)
        {
            var nodesByPath = new Dictionary<string, TreeNode>();
            var roots = new List<TreeNode>();

            foreach (var row in rows)
            {
                var segments = row.DestinationPath.Split('/');
                var currentPath = string.Empty;
                List<TreeNode> siblings = roots;
                TreeNode node = null;

                for (var i = 0; i < segments.Length; i++)
                {
                    currentPath = i == 0 ? segments[0] : currentPath + "/" + segments[i];

                    if (!nodesByPath.TryGetValue(currentPath, out node))
                    {
                        node = new TreeNode { Name = segments[i] };
                        nodesByPath.Add(currentPath, node);
                        siblings.Add(node);
                    }

                    siblings = node.Children;
                }

                // node now refers to the leaf matching row.DestinationPath.
                if (node != null)
                    node.Row = row;
            }

            SortTree(roots);
            return roots;
        }

        private static void SortTree(List<TreeNode> nodes)
        {
            nodes.Sort((a, b) =>
            {
                var aIsFolder = a.Children.Count > 0;
                var bIsFolder = b.Children.Count > 0;
                if (aIsFolder != bIsFolder)
                    return aIsFolder ? -1 : 1;
                return string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
            });
            foreach (var node in nodes)
                SortTree(node.Children);
        }

        private void OnGUI()
        {
            if (_rows == null || _roots == null)
            {
                // Fields populated only in Open() don't survive a script recompile's domain reload
                // if this window was left open across it - show a clean message instead of an NRE.
                EditorGUILayout.HelpBox("This import session is no longer valid. Please reopen it via " +
                    "Assets/Import Package/Extract Here.", MessageType.Info);
                if (GUILayout.Button("Close"))
                    Close();
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Package", _packagePath);
                EditorGUILayout.LabelField("Destination", _selectedFolder);
            }

            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("All", GUILayout.Width(60)))
                    SetAllEnabled(true);
                if (GUILayout.Button("None", GUILayout.Width(60)))
                    SetAllEnabled(false);
            }

            EditorGUILayout.Space(2);

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, EditorStyles.helpBox);
            foreach (var root in _roots)
                DrawNode(root, 0);
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            DrawSeparator();
            EditorGUILayout.Space(4);

            var enabledCount = CountEnabled();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = enabledCount > 0;
                if (GUILayout.Button("Import (" + enabledCount + ")"))
                    Import();
                GUI.enabled = true;
                if (GUILayout.Button("Cancel"))
                    Close();
            }
        }

        private static void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            var lineColor = EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.15f) : new Color(0f, 0f, 0f, 0.2f);
            EditorGUI.DrawRect(rect, lineColor);
        }

        private const float ArrowWidth = 16f;

        private void DrawNode(TreeNode node, int depth)
        {
            var isFolder = node.Children.Count > 0;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 16);

                // A real EditorGUILayout.Foldout doesn't reserve the same width as a plain
                // GUILayout.Space placeholder, so folder and leaf rows drifted out of alignment.
                // Drawing the arrow as a fixed-width icon button keeps every row's icon/name aligned.
                if (isFolder)
                {
                    if (GUILayout.Button(GetArrowIcon(node.Expanded), GUIStyle.none,
                        GUILayout.Width(ArrowWidth), GUILayout.Height(ArrowWidth)))
                        node.Expanded = !node.Expanded;
                }
                else
                {
                    GUILayout.Space(ArrowWidth);
                }

                // Deliberately binary (checked/empty only) - no mixed-state dash, even when a
                // folder's children are partially selected.
                var wasChecked = GetNodeState(node) == 2;
                var isChecked = EditorGUILayout.Toggle(wasChecked, GUILayout.Width(16));
                if (isChecked != wasChecked)
                {
                    SetNodeEnabled(node, isChecked);
                    // Ancestor folder rows are drawn before their children in this same pass, so
                    // without a forced repaint a parent's checkbox keeps showing its pre-click state
                    // until some later, unrelated GUI event happens to redraw the window.
                    Repaint();
                }

                GUILayout.Label(GetIcon(node), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Label(node.Name);

                GUILayout.FlexibleSpace();
                DrawTag(node);
            }

            if (isFolder && node.Expanded)
            {
                foreach (var child in node.Children)
                    DrawNode(child, depth + 1);
            }
        }

        /// <summary>0 = none enabled, 1 = mixed, 2 = all enabled.</summary>
        private static int GetNodeState(TreeNode node)
        {
            var total = 0;
            var enabledCount = 0;
            CountState(node, ref total, ref enabledCount);
            if (total == 0 || enabledCount == 0)
                return 0;
            return enabledCount == total ? 2 : 1;
        }

        private static void CountState(TreeNode node, ref int total, ref int enabledCount)
        {
            if (node.Row != null)
            {
                total++;
                if (node.Row.Enabled)
                    enabledCount++;
            }
            foreach (var child in node.Children)
                CountState(child, ref total, ref enabledCount);
        }

        private static void SetNodeEnabled(TreeNode node, bool enabled)
        {
            if (node.Row != null)
                node.Row.Enabled = enabled;
            foreach (var child in node.Children)
                SetNodeEnabled(child, enabled);
        }

        /// <summary>"d_forward" / "d_icon dropdown" - built-in icon names from
        /// https://github.com/halak/unity-editor-icons.</summary>
        private GUIContent GetArrowIcon(bool expanded)
        {
            if (expanded)
            {
                if (_expandedArrowIcon == null)
                    _expandedArrowIcon = EditorGUIUtility.IconContent("d_icon dropdown@2x");
                return _expandedArrowIcon;
            }

            if (_collapsedArrowIcon == null)
                _collapsedArrowIcon = EditorGUIUtility.IconContent("d_forward@2x");
            return _collapsedArrowIcon;
        }

        private GUIContent GetIcon(TreeNode node)
        {
            if (node.Children.Count > 0)
            {
                if (_folderIcon == null)
                    _folderIcon = EditorGUIUtility.IconContent("Folder Icon");
                return _folderIcon;
            }

            if (node.Row != null && node.Row.Exists)
            {
                var cached = AssetDatabase.GetCachedIcon(node.Row.ExistingAssetPath);
                if (cached != null)
                    return new GUIContent(cached);
            }

            var extension = GetSafeExtension(node.Name);
            if (!string.IsNullOrEmpty(extension) && ExtensionIconNames.TryGetValue(extension, out var iconName))
            {
                var content = EditorGUIUtility.IconContent(iconName);
                if (content != null && content.image != null)
                    return content;
            }

            if (_defaultAssetIcon == null)
                _defaultAssetIcon = EditorGUIUtility.IconContent("DefaultAsset Icon");
            return _defaultAssetIcon;
        }

        /// <summary>
        /// Archive entry names come from arbitrary text inside the .unitypackage, not the filesystem,
        /// so they can contain characters System.IO.Path rejects (e.g. Path.GetExtension throws
        /// "Illegal characters in path" on some real-world packages) - this never touches disk, so it
        /// can't use those APIs.
        /// </summary>
        private static string GetSafeExtension(string fileName)
        {
            var lastDot = fileName.LastIndexOf('.');
            if (lastDot < 0 || lastDot == fileName.Length - 1)
                return string.Empty;
            return fileName.Substring(lastDot);
        }

        private void DrawTag(TreeNode node)
        {
            if (node.Row == null || node.Children.Count > 0)
                return;

            string text;
            Color background;
            if (!node.Row.Exists)
            {
                text = "New";
                background = new Color(0.20f, 0.45f, 0.20f);
            }
            else if (node.Row.ExistingAssetPath != node.Row.DestinationPath)
            {
                text = "Move";
                background = new Color(0.55f, 0.4f, 0.1f);
            }
            else
            {
                text = "Replace";
                background = new Color(0.5f, 0.3f, 0.1f);
            }

            if (_tagStyle == null)
            {
                _tagStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    normal = { textColor = Color.white }
                };
            }

            var rect = GUILayoutUtility.GetRect(50, 16, GUILayout.Width(50));
            EditorGUI.DrawRect(rect, background);
            GUI.Label(rect, text, _tagStyle);
        }

        private void SetAllEnabled(bool enabled)
        {
            foreach (var row in _rows)
                row.Enabled = enabled;
        }

        private int CountEnabled()
        {
            var count = 0;
            foreach (var row in _rows)
                if (row.Enabled)
                    count++;
            return count;
        }

        private void Import()
        {
            var targets = new Dictionary<string, PackageExtractionTarget>();
            foreach (var row in _rows)
            {
                if (!row.Enabled)
                    continue;

                if (row.Exists && row.ExistingAssetPath != row.DestinationPath)
                    AssetDatabase.DeleteAsset(row.ExistingAssetPath);

                targets[row.Entry.Guid] = new PackageExtractionTarget
                {
                    AssetPath = row.DestinationPath,
                    IsFolder = row.Entry.IsFolder
                };
            }

            UnityPackageArchive.ExtractAssets(_packagePath, targets);
            AssetDatabase.Refresh();
            Close();
        }
    }
}
