using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>
    /// Open, view and edit Figma <c>.fig</c> files in the Unity Editor, laid out like figcraft /
    /// Figma: an icon rail and the Layers / Assets / File panel on the left, the canvas in the
    /// middle (rendered in the background, see <see cref="FigTileCache"/>), and the Design /
    /// Prototype / Inspect panel on the right.
    ///
    /// The file is decoded generically with the schema it carries (see <see cref="FigFile"/>);
    /// instances are expanded from their main components (<see cref="FigScene"/>) and the page is
    /// drawn by a software renderer (<see cref="FigRenderer"/>). Format knowledge comes from figcraft
    /// (https://github.com/storytold/figcraft).
    ///
    /// Open via: Tools &gt; Thinhnv &gt; Fig Editor, or right-click a .fig asset &gt; Open in Fig Editor.
    /// </summary>
    public partial class FigEditorWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Thinhnv/Fig Editor";
        private const float RailWidth = 48f;
        private const float HeaderHeight = 40f;
        private const float MinSideWidth = 200f;

        private enum Rail
        {
            Layers,
            Assets,
            File,
        }

        private enum RightTab
        {
            Design,
            Prototype,
            Inspect,
        }

        [SerializeField] private string filePath;
        [SerializeField] private string selectedId;
        [SerializeField] private float leftWidth = 260f;
        [SerializeField] private float rightWidth = 280f;
        [SerializeField] private Rail rail = Rail.Layers;
        [SerializeField] private RightTab rightTab = RightTab.Design;
        [SerializeField] private List<string> expandedIds = new();
        [SerializeField] private List<string> recentFiles = new();

        private FigFile file;
        private string loadError;
        private bool backupMade;
        private int resizing;

        private FigNode Selected => file?.Find(selectedId);

        [MenuItem(MenuPath)]
        public static void Open()
        {
            GetWindow<FigEditorWindow>("Fig Editor").minSize = new Vector2(900, 500);
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
            window.minSize = new Vector2(900, 500);
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
            EnableCanvas();

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
            DisableCanvas();
            ClearTextureCache();
            FigTheme.ReleaseIcons();
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

                ClearTextureCache();
                ResetCanvas();
                OnStructureChanged();
                foreach (FigNode root in file.Roots)
                {
                    expanded.Add(root.Id);
                }

                recentFiles.Remove(path);
                recentFiles.Insert(0, path);
                if (recentFiles.Count > 8)
                {
                    recentFiles.RemoveRange(8, recentFiles.Count - 8);
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

        /// <summary>Select the project config in the Inspector, creating it when the project has none.</summary>
        private static void ShowProjectConfig()
        {
            FigProjectConfig config = FigProjectConfig.Instance;
            if (config == null && !EditorUtility.DisplayDialog("Fig Editor", "Create the project config at " + FigProjectConfig.DefaultPath + "? It keeps the design files, their Figma links and texture exports.", "Create", "Cancel"))
            {
                return;
            }

            config = FigProjectConfig.GetOrCreate();
            Selection.activeObject = config;
            EditorGUIUtility.PingObject(config);
        }

        [MenuItem("Tools/Thinhnv/Fig Editor - Project Config", false, 102)]
        private static void ProjectConfigMenu()
        {
            ShowProjectConfig();
        }

        /// <summary>Fonts were installed or added to the project: index them again and lay the text out again.</summary>
        private void RescanFonts()
        {
            FigFonts.Reset();
            FigTextLayout.ClearCache();
            // Rebuild the scene (and its tiles) without touching the edit history.
            tileCache?.SetScene(null, false);
            scene = null;
            boardDirty = true;
            Repaint();
            Debug.Log("Fig Editor: " + FigFonts.Count + " font faces found.");
        }

        /// <summary>Where the open file came from, when it was imported from a Figma link.</summary>
        private FigmaSource Source
        {
            get
            {
                if (file != file4Source)
                {
                    file4Source = file;
                    cachedSource = FigmaSource.FromFile(file);
                }

                return cachedSource;
            }
        }

        private FigFile file4Source;
        private FigmaSource cachedSource;

        private void UpdateFromFigma()
        {
            FigmaSource from = Source;
            if (from == null)
            {
                return;
            }

            if (hasUnsavedChanges && !EditorUtility.DisplayDialog("Update from Figma",
                    $"{Path.GetFileName(filePath)} has unsaved edits. Updating replaces the file with Figma's current version, so they are lost.", "Continue", "Cancel"))
            {
                return;
            }

            FigImportWindow.ShowUpdate(filePath, from);
        }

        /// <summary>Show a file the Figma import just wrote (and its linked layer), or reload it after an update.</summary>
        public static void OpenImported(string path, string nodeId, bool update)
        {
            var w = GetWindow<FigEditorWindow>();
            w.Show();
            bool same = w.file != null && !string.IsNullOrEmpty(w.filePath) &&
                        string.Equals(Path.GetFullPath(w.filePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (same && update)
            {
                // The user already agreed to replace the edits.
                w.hasUnsavedChanges = false;
                w.LoadFrom(path, quiet: false);
            }
            else
            {
                w.OpenFile(path);
            }

            if (!string.IsNullOrEmpty(nodeId) && w.file?.Find(nodeId) != null)
            {
                w.Select(nodeId);
                w.pendingZoomTo = nodeId;
            }

            w.Repaint();
        }

        /// <summary>Drop every edit since the last save: read the file from disk again (history cleared).</summary>
        private void DiscardChanges()
        {
            if (file == null || !hasUnsavedChanges ||
                !EditorUtility.DisplayDialog("Fig Editor", $"Discard all unsaved edits to {Path.GetFileName(filePath)}?\n\nThis can't be undone.", "Discard", "Cancel"))
            {
                return;
            }

            hasUnsavedChanges = false;
            LoadFrom(filePath, quiet: false);
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

        /// <summary>Call after anything that changes the layer tree (ids, parents, positions, names, visibility).</summary>
        private void OnStructureChanged()
        {
            rowsDirty = true;
            imageUsers = null;
            InvalidateBoard();
        }

        private void ShowFileMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open…"), false, () => Defer(ShowOpenDialog));
            menu.AddItem(new GUIContent("Import from Figma Link…"), false, () => Defer(() => FigImportWindow.ShowImport(null)));
            if (Source != null)
            {
                menu.AddItem(new GUIContent("Update from Figma…"), false, () => Defer(UpdateFromFigma));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Update from Figma…"));
            }

            menu.AddItem(new GUIContent("Figma Access Token…"), false, FigmaSettings.Open);
            menu.AddItem(new GUIContent("Rescan Installed Fonts"), false, () => Defer(RescanFonts));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Project Config…"), false, () => Defer(ShowProjectConfig));
            if (file != null)
            {
                menu.AddItem(new GUIContent("Add File to Project Config"), false, () => Defer(() =>
                {
                    FigProjectConfig.GetOrCreate().AddFile(filePath, Source);
                    ShowProjectConfig();
                }));
            }

            FigProjectConfig.DesignFile linkedFile = FigProjectConfig.Instance?.FindFile(filePath);
            if (linkedFile != null && linkedFile.textures.Count > 0)
            {
                menu.AddItem(new GUIContent("Export Linked Textures (" + linkedFile.textures.Count + ")"), false, () => Defer(() =>
                    FigTextureExport.ExportTargets(FigProjectConfig.Instance, file, linkedFile, linkedFile.textures, false)));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Export Linked Textures"));
            }
            foreach (string recent in recentFiles)
            {
                if (recent != filePath && File.Exists(recent))
                {
                    string path = recent;
                    menu.AddItem(new GUIContent("Open Recent/" + Path.GetFileName(path).Replace("/", "∕")), false, () => Defer(() => OpenFile(path)));
                }
            }

            menu.AddSeparator("");
            if (file != null)
            {
                menu.AddItem(new GUIContent("Save"), false, () => Defer(() => SaveTo(filePath)));
                menu.AddItem(new GUIContent("Save As…"), false, () => Defer(ShowSaveAsDialog));
                if (hasUnsavedChanges)
                {
                    menu.AddItem(new GUIContent("Discard Changes"), false, () => Defer(DiscardChanges));
                }
                else
                {
                    menu.AddItem(new GUIContent("Reload"), false, () => Defer(() => LoadFrom(filePath, quiet: false)));
                }
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Export Message JSON…"), false, () => Defer(() => ExportJson(file.Message, Path.GetFileNameWithoutExtension(filePath) + ".message")));
                menu.AddItem(new GUIContent("Export Schema…"), false, () => Defer(ExportSchemaText));
                menu.AddItem(new GUIContent("Show in Explorer"), false, () => EditorUtility.RevealInFinder(filePath));
            }

            menu.ShowAsContext();
        }

        // ─── GUI ─────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            var all = new Rect(0, 0, position.width, position.height);
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(all, FigTheme.Rail);
            }

            if (file == null)
            {
                DrawEmptyState(all);
                return;
            }

            leftWidth = Mathf.Clamp(leftWidth, MinSideWidth, Mathf.Max(MinSideWidth, position.width * 0.4f));
            rightWidth = Mathf.Clamp(rightWidth, 240f, Mathf.Max(240f, position.width * 0.4f));
            var railRect = new Rect(0, 0, RailWidth, position.height);
            var leftRect = new Rect(railRect.xMax, 0, leftWidth, position.height);
            var rightRect = new Rect(position.width - rightWidth, 0, rightWidth, position.height);
            var canvasRect = new Rect(leftRect.xMax + 1, 0, rightRect.x - leftRect.xMax - 2, position.height);

            DrawRail(railRect);
            DrawCanvas(canvasRect);
            DrawLeftPanel(leftRect);
            DrawRightPanel(rightRect);
            HandleSplitter(new Rect(leftRect.xMax - 2, 0, 5, position.height), 1);
            HandleSplitter(new Rect(rightRect.x - 3, 0, 5, position.height), 2);
        }

        private void DrawRail(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, FigTheme.Rail);
                EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), FigTheme.Border);
            }

            float y = 10;
            RailButton(new Rect(r.x + 6, y, 36, 46), "layers", "Layers", Rail.Layers);
            RailButton(new Rect(r.x + 6, y + 52, 36, 46), "assets", "Assets", Rail.Assets);
            RailButton(new Rect(r.x + 6, y + 104, 36, 46), "file", "File", Rail.File);
        }

        private void RailButton(Rect r, string icon, string label, Rail which)
        {
            var iconRect = new Rect(r.x, r.y, r.width, 30);
            bool active = rail == which;
            if (Event.current.type == EventType.Repaint && active)
            {
                EditorGUI.DrawRect(new Rect(iconRect.x + 2, iconRect.y, iconRect.width - 4, iconRect.height), FigTheme.Hover);
            }

            if (FigTheme.IconButton(iconRect, icon, label, false, active ? FigTheme.Text : FigTheme.TextDim))
            {
                rail = which;
                GUI.FocusControl(null);
            }

            GUI.Label(new Rect(r.x - 4, iconRect.yMax, r.width + 8, 14), label, active ? FigTheme.Small : FigTheme.Faint);
        }

        private void DrawEmptyState(Rect all)
        {
            var box = new Rect(all.center.x - 210, all.center.y - 120, 420, 240 + recentFiles.Count * 22);
            GUI.Label(new Rect(box.x, box.y, box.width, 24), "Fig Editor", FigTheme.Title);
            GUI.Label(new Rect(box.x, box.y + 26, box.width, 36), "Open a Figma .fig file (File › Save local copy… in Figma) to view and edit it.", FigTheme.Dim);
            if (!string.IsNullOrEmpty(loadError))
            {
                EditorGUI.HelpBox(new Rect(box.x, box.y + 64, box.width, 40), loadError, MessageType.Error);
            }

            if (FigTheme.AccentButton(new Rect(box.x, box.y + 112, 160, 32), "Open .fig file…"))
            {
                Defer(ShowOpenDialog);
            }

            if (GUI.Button(new Rect(box.x + 170, box.y + 112, 190, 32), "Import from Figma link…"))
            {
                Defer(() => FigImportWindow.ShowImport(null));
            }

            float y = box.y + 160;
            if (recentFiles.Count > 0)
            {
                GUI.Label(new Rect(box.x, y, box.width, 18), "Recent", FigTheme.Small);
                y += 20;
                foreach (string recent in recentFiles.ToList())
                {
                    if (!File.Exists(recent))
                    {
                        continue;
                    }

                    if (FigTheme.FlatButton(new Rect(box.x, y, box.width, 20), "  " + Path.GetFileName(recent), FigTheme.Label))
                    {
                        string p = recent;
                        Defer(() => OpenFile(p));
                    }

                    y += 22;
                }
            }

            HandleFileDrop(all);
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

        private void HandleSplitter(Rect r, int which)
        {
            EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                resizing = which;
                e.Use();
            }
            else if (resizing == which && e.type == EventType.MouseDrag)
            {
                if (which == 1)
                {
                    leftWidth = e.mousePosition.x - RailWidth;
                }
                else
                {
                    rightWidth = position.width - e.mousePosition.x;
                }

                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && resizing == which)
            {
                resizing = 0;
            }
        }

        private void DrawLeftPanel(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, FigTheme.Panel);
                EditorGUI.DrawRect(new Rect(r.xMax, r.y, 1, r.height), FigTheme.Border);
            }

            // File header: menu, name, status.
            if (FigTheme.IconButton(new Rect(r.x + 8, r.y + 8, 24, 24), "menu", "File menu"))
            {
                ShowFileMenu();
            }

            string name = Path.GetFileNameWithoutExtension(filePath);
            FigmaSource linked = Source;
            float labelWidth = r.width - 46 - (linked != null ? 28 : 0);
            GUI.Label(new Rect(r.x + 38, r.y + 6, labelWidth, 16), name, FigTheme.Bold);
            GUI.Label(new Rect(r.x + 38, r.y + 22, labelWidth, 14), hasUnsavedChanges ? "Edited — not saved" : linked != null ? "Saved · from Figma" : "Saved", FigTheme.Faint);
            if (linked != null && FigTheme.IconButton(new Rect(r.xMax - 30, r.y + 8, 24, 24), "rotate", "Update from Figma (" + (linked.Name ?? linked.FileKey) + ")"))
            {
                Defer(UpdateFromFigma);
            }
            FigTheme.Separator(new Rect(r.x, r.y + HeaderHeight + 4, r.width, 1));

            var body = new Rect(r.x, r.y + HeaderHeight + 5, r.width, r.height - HeaderHeight - 5);
            switch (rail)
            {
                case Rail.Layers:
                    DrawLayersPanel(body);
                    break;
                case Rail.Assets:
                    GUILayout.BeginArea(body);
                    DrawAssetsPanel(body);
                    GUILayout.EndArea();
                    break;
                default:
                    GUILayout.BeginArea(new Rect(body.x + 8, body.y + 4, body.width - 12, body.height - 4));
                    DrawFileTab();
                    GUILayout.EndArea();
                    break;
            }
        }

        private void DrawRightPanel(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(r, FigTheme.Panel);
                EditorGUI.DrawRect(new Rect(r.x - 1, r.y, 1, r.height), FigTheme.Border);
            }

            // Header: undo / redo, zoom, save.
            if (FigTheme.IconButton(new Rect(r.x + 8, r.y + 8, 24, 24), "undo", undo.CanUndo ? "Undo " + undo.UndoLabel : "Nothing to undo") && undo.CanUndo)
            {
                UndoStep(false);
            }

            if (FigTheme.IconButton(new Rect(r.x + 34, r.y + 8, 24, 24), "redo", undo.CanRedo ? "Redo " + undo.RedoLabel : "Nothing to redo") && undo.CanRedo)
            {
                UndoStep(true);
            }

            // Save, with Discard beside it while there are unsaved edits.
            float discardWidth = hasUnsavedChanges ? 60f : 0f;
            var zoomRect = new Rect(r.xMax - 124 - discardWidth, r.y + 8, 50, 24);
            if (FigTheme.FlatButton(zoomRect, Mathf.RoundToInt(viewScale * 100f) + "% ▾", FigTheme.Label))
            {
                ShowZoomMenu();
            }

            if (hasUnsavedChanges && GUI.Button(new Rect(r.xMax - 70 - discardWidth, r.y + 9, discardWidth - 4, 22), new GUIContent("Discard", "Discard all unsaved edits and read the file again"), EditorStyles.miniButton))
            {
                Defer(DiscardChanges);
            }

            if (FigTheme.AccentButton(new Rect(r.xMax - 70, r.y + 8, 62, 24), hasUnsavedChanges ? "Save*" : "Save"))
            {
                Defer(() => SaveTo(filePath));
            }

            // Tabs.
            float tx = r.x + 8;
            foreach (RightTab t in new[] { RightTab.Design, RightTab.Prototype, RightTab.Inspect })
            {
                var tr = new Rect(tx, r.y + HeaderHeight + 2, 72, 24);
                bool active = rightTab == t;
                if (Event.current.type == EventType.Repaint && active)
                {
                    EditorGUI.DrawRect(tr, FigTheme.Hover);
                }

                GUI.Label(tr, t.ToString(), active ? FigTheme.TabActive : FigTheme.Tab);
                if (Event.current.type == EventType.MouseDown && tr.Contains(Event.current.mousePosition))
                {
                    rightTab = t;
                    GUI.FocusControl(null);
                    Event.current.Use();
                }

                tx += 76;
            }

            FigTheme.Separator(new Rect(r.x, r.y + HeaderHeight + 30, r.width, 1));
            var body = new Rect(r.x, r.y + HeaderHeight + 31, r.width, r.height - HeaderHeight - 31);
            GUILayout.BeginArea(body);
            switch (rightTab)
            {
                case RightTab.Design:
                    DrawDesignPanel(body.width);
                    break;
                case RightTab.Prototype:
                    DrawPrototypePanel(body.width);
                    break;
                default:
                    DrawInspectPanel(body.width);
                    break;
            }

            GUILayout.EndArea();
        }

        /// <summary>Select <paramref name="id"/> and expand its ancestors so the layers panel shows it.</summary>
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
