using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Open, inspect and edit Figma <c>.fig</c> files inside the Unity Editor: browse the layer
    /// tree, edit any field of a layer (names, visibility, sizes, positions, text, colours, enums —
    /// or any raw field the file's schema defines), edit pages on a Figma-like board, replace or export
    /// images, edit <c>meta.json</c> and the thumbnail, then save back to <c>.fig</c>.
    ///
    /// The file is decoded generically with the schema it carries (see <see cref="FigFile"/>), so
    /// fields this tool has no UI for are still shown in the raw view and written back unchanged.
    /// Format knowledge comes from figcraft (https://github.com/storytold/figcraft).
    ///
    /// Open via: Tools &gt; Thinhnv &gt; Fig Editor, or right-click a .fig asset &gt; Open in Fig Editor.
    /// </summary>
    public partial class FigEditorWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Thinhnv/Fig Editor";
        private const float ToolbarHeight = 21f;
        private const float MinTreeWidth = 180f;

        private static readonly string[] TabNames = { "Layer", "Board", "Images", "File" };

        [SerializeField] private string filePath;
        [SerializeField] private string selectedId;
        [SerializeField] private int tab;
        [SerializeField] private float treeWidth = 320f;
        [SerializeField] private List<string> expandedIds = new();

        private FigFile file;
        private string loadError;
        private bool backupMade;
        private bool resizingTree;
        private int imageCount = -1;

        private GUIStyle richMiniLabel;
        private GUIStyle wrapLabel;

        private FigNode Selected => file?.Find(selectedId);

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<FigEditorWindow>("Fig Editor").minSize = new Vector2(760, 420);
        }

        [MenuItem("Assets/Open in Fig Editor", false, 40)]
        private static void OpenSelectedAsset()
        {
            string path = SelectedFigAssetPath();
            if (path == null)
            {
                return;
            }

            FigEditorWindow window = GetWindow<FigEditorWindow>("Fig Editor");
            window.minSize = new Vector2(760, 420);
            window.OpenFile(Path.GetFullPath(path));
        }

        [MenuItem("Assets/Open in Fig Editor", true)]
        private static bool OpenSelectedAssetValidate()
        {
            return SelectedFigAssetPath() != null;
        }

        private static string SelectedFigAssetPath()
        {
            string path = Selection.activeObject != null ? AssetDatabase.GetAssetPath(Selection.activeObject) : null;
            return !string.IsNullOrEmpty(path) && path.EndsWith(".fig", StringComparison.OrdinalIgnoreCase) ? path : null;
        }

        private void OnEnable()
        {
            saveChangesMessage = "The .fig file open in Fig Editor has unsaved edits. Save them before closing?";
            expanded = new HashSet<string>(expandedIds);
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EnableBoard();

            // Unsaved edits don't survive a domain reload (the decoded tree isn't serializable), so
            // the file is simply read again from disk.
            if (file == null && !string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                LoadFrom(filePath, quiet: true);
            }
        }

        private void OnDisable()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            expandedIds = new List<string>(expanded);
            DisableBoard();
            ClearTextureCache();
        }

        private void OnBeforeAssemblyReload()
        {
            if (file != null && hasUnsavedChanges &&
                EditorUtility.DisplayDialog("Fig Editor", $"Scripts are reloading. Save the edits to {Path.GetFileName(filePath)} first?\n\nUnsaved edits are lost otherwise.", "Save", "Discard"))
            {
                SaveTo(filePath);
            }
        }

        public override void SaveChanges()
        {
            if (file != null && SaveTo(filePath))
            {
                base.SaveChanges();
            }
        }

        /// <summary>Queue an action to run outside the current IMGUI event, then repaint.</summary>
        private void Defer(Action action)
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null)
                {
                    return;
                }

                action();
                Repaint();
            };
        }

        // ─── File operations ─────────────────────────────────────────────────────

        public void OpenFile(string path)
        {
            if (!ConfirmDiscard())
            {
                return;
            }

            LoadFrom(path, quiet: false);
        }

        private bool ConfirmDiscard()
        {
            if (file == null || !hasUnsavedChanges)
            {
                return true;
            }

            int choice = EditorUtility.DisplayDialogComplex("Fig Editor", $"Save the edits to {Path.GetFileName(filePath)} first?", "Save", "Cancel", "Discard");
            if (choice == 1)
            {
                return false;
            }

            return choice == 2 || SaveTo(filePath);
        }

        private void LoadFrom(string path, bool quiet)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Fig Editor", "Reading " + Path.GetFileName(path) + "…", 0.5f);
                FigFile loaded = FigFile.Load(path);
                file = loaded;
                filePath = path;
                loadError = null;
                backupMade = false;
                hasUnsavedChanges = false;
                if (Selected == null)
                {
                    selectedId = null;
                }

                OnStructureChanged();
                ClearTextureCache();
                ResetBoard();
                foreach (FigNode root in file.Roots)
                {
                    expanded.Add(root.Id);
                }

                titleContent = new GUIContent("Fig Editor", "Fig Editor — " + Path.GetFileName(path));
            }
            catch (Exception e)
            {
                loadError = $"Could not read {Path.GetFileName(path)}: {e.Message}";
                if (!quiet)
                {
                    Debug.LogException(e);
                    EditorUtility.DisplayDialog("Fig Editor", loadError, "OK");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ShowOpenDialog()
        {
            string dir = string.IsNullOrEmpty(filePath) ? Application.dataPath : Path.GetDirectoryName(filePath);
            string path = EditorUtility.OpenFilePanel("Open .fig file", dir, "fig");
            if (!string.IsNullOrEmpty(path))
            {
                Defer(() => OpenFile(path));
            }
        }

        private bool SaveTo(string path)
        {
            if (file == null || string.IsNullOrEmpty(path))
            {
                return false;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Fig Editor", "Writing " + Path.GetFileName(path) + "…", 0.5f);

                // One backup of the original per session, before the first overwrite.
                if (!backupMade && File.Exists(path) && path == file.SourcePath)
                {
                    File.Copy(path, path + ".bak", true);
                    backupMade = true;
                }

                file.Save(path);
                filePath = path;
                hasUnsavedChanges = false;
                RefreshIfInProject(path);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Fig Editor", $"Could not save {Path.GetFileName(path)}: {e.Message}", "OK");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ShowSaveAsDialog()
        {
            string dir = Path.GetDirectoryName(filePath);
            string name = Path.GetFileNameWithoutExtension(filePath) + " edited";
            string path = EditorUtility.SaveFilePanel("Save .fig file as", dir, name, "fig");
            if (!string.IsNullOrEmpty(path))
            {
                Defer(() =>
                {
                    if (SaveTo(path))
                    {
                        backupMade = true;
                    }
                });
            }
        }

        private static void RefreshIfInProject(string path)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            string assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase))
            {
                AssetDatabase.ImportAsset("Assets" + full.Substring(assets.Length));
            }
        }

        private void MarkDirty()
        {
            hasUnsavedChanges = true;
        }

        /// <summary>Call after anything that changes the layer tree (ids, parents, positions, names).</summary>
        private void OnStructureChanged()
        {
            rowsDirty = true;
            imageUsers = null;
            InvalidateBoard();
            imageCount = -1;
        }

        // ─── GUI ─────────────────────────────────────────────────────────────────

        private void InitStyles()
        {
            if (richMiniLabel != null)
            {
                return;
            }

            richMiniLabel = new GUIStyle(EditorStyles.miniLabel) { richText = true };
            wrapLabel = new GUIStyle(EditorStyles.label) { wordWrap = true };
        }

        private void OnGUI()
        {
            InitStyles();
            DrawToolbar();

            if (file == null)
            {
                DrawEmptyState();
                return;
            }

            var body = new Rect(0, ToolbarHeight, position.width, position.height - ToolbarHeight);
            treeWidth = Mathf.Clamp(treeWidth, MinTreeWidth, Mathf.Max(MinTreeWidth, position.width - 320f));
            var treeRect = new Rect(body.x, body.y, treeWidth, body.height);
            var splitter = new Rect(treeRect.xMax, body.y, 4f, body.height);
            var rightRect = new Rect(splitter.xMax, body.y, body.width - splitter.xMax, body.height);

            DrawTree(treeRect);
            DrawSplitter(splitter);

            GUILayout.BeginArea(rightRect);
            DrawRightPanel(rightRect);
            GUILayout.EndArea();
        }

        private void DrawToolbar()
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button("Open…", EditorStyles.toolbarButton, GUILayout.Width(56)))
            {
                Defer(ShowOpenDialog);
            }

            using (new EditorGUI.DisabledScope(file == null))
            {
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(54)))
                {
                    Defer(() =>
                    {
                        if (!hasUnsavedChanges || EditorUtility.DisplayDialog("Fig Editor", "Discard the unsaved edits and read the file again?", "Reload", "Cancel"))
                        {
                            hasUnsavedChanges = false;
                            LoadFrom(filePath, quiet: false);
                        }
                    });
                }

                if (GUILayout.Button(hasUnsavedChanges ? "Save*" : "Save", EditorStyles.toolbarButton, GUILayout.Width(48)))
                {
                    Defer(() => SaveTo(filePath));
                }

                if (GUILayout.Button("Save As…", EditorStyles.toolbarButton, GUILayout.Width(66)))
                {
                    Defer(ShowSaveAsDialog);
                }
            }

            GUILayout.Space(8);
            if (file != null)
            {
                GUILayout.Label(new GUIContent(Path.GetFileName(filePath), filePath), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (imageCount < 0)
                {
                    imageCount = file.Images.Count();
                }

                GUILayout.Label($"{file.Nodes.Count} layers · {imageCount} images", EditorStyles.miniLabel);
            }
            else
            {
                GUILayout.FlexibleSpace();
            }

            GUILayout.EndHorizontal();
        }

        private void DrawEmptyState()
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(420));
            GUILayout.Label("Fig Editor", EditorStyles.boldLabel);
            GUILayout.Label("Open a Figma .fig file (File > Save local copy… in Figma) to browse its layers, edit fields, preview frames and replace images.", wrapLabel);
            if (!string.IsNullOrEmpty(loadError))
            {
                EditorGUILayout.HelpBox(loadError, MessageType.Error);
            }

            GUILayout.Space(6);
            if (GUILayout.Button("Open .fig file…", GUILayout.Height(28)))
            {
                Defer(ShowOpenDialog);
            }

            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();

            Rect drop = new Rect(0, ToolbarHeight, position.width, position.height - ToolbarHeight);
            HandleFileDrop(drop);
        }

        private void HandleFileDrop(Rect area)
        {
            Event e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !area.Contains(e.mousePosition))
            {
                return;
            }

            string path = null;
            foreach (string p in DragAndDrop.paths)
            {
                if (p.EndsWith(".fig", StringComparison.OrdinalIgnoreCase))
                {
                    path = p;
                    break;
                }
            }

            if (path == null)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                string full = Path.GetFullPath(path);
                Defer(() => OpenFile(full));
            }

            e.Use();
        }

        private void DrawSplitter(Rect rect)
        {
            EditorGUI.DrawRect(new Rect(rect.x + 1, rect.y, 1, rect.height), new Color(0f, 0f, 0f, 0.3f));
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
            Event e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                resizingTree = true;
                e.Use();
            }
            else if (resizingTree && e.type == EventType.MouseDrag)
            {
                treeWidth = e.mousePosition.x;
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp)
            {
                resizingTree = false;
            }
        }

        private void DrawRightPanel(Rect rect)
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);
            int newTab = GUILayout.Toolbar(tab, TabNames, EditorStyles.toolbarButton);
            if (newTab != tab)
            {
                tab = newTab;
                GUI.FocusControl(null);
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            var content = new Rect(0, ToolbarHeight, rect.width, rect.height - ToolbarHeight);
            switch (tab)
            {
                case 0:
                    DrawLayerTab();
                    break;
                case 1:
                    DrawBoardTab(content);
                    break;
                case 2:
                    DrawImagesTab();
                    break;
                default:
                    DrawFileTab();
                    break;
            }
        }

        /// <summary>Select <paramref name="id"/> and expand its ancestors so the tree shows it.</summary>
        private void Select(string id, bool reveal = true)
        {
            selectedId = id;
            GUI.FocusControl(null);
            if (!reveal)
            {
                return;
            }

            for (FigNode n = file?.Find(id)?.Parent; n != null; n = n.Parent)
            {
                if (expanded.Add(n.Id))
                {
                    rowsDirty = true;
                }
            }

            scrollToSelection = true;
        }
    }
}
