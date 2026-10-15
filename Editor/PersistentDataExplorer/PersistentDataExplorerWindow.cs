using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.PersistentDataExplorer
{
    /// <summary>
    /// Browse and manage the files under <see cref="Application.persistentDataPath"/> without
    /// leaving the Editor: navigate folders, search recursively, create / rename / duplicate /
    /// delete, import (button or drag &amp; drop from Explorer/Project window), export, and view or
    /// edit text files in place — with a JSON formatter for minified save files.
    ///
    /// Every action that changes what the window draws (selection, navigation, file operations,
    /// dialogs) is deferred to <see cref="EditorApplication.delayCall"/> so it never runs in the
    /// middle of an IMGUI pass — that avoids layout-mismatch errors and the "EndLayoutGroup"
    /// errors modal dialogs cause when opened from inside OnGUI.
    ///
    /// Open via: Tools &gt; Thinhnv &gt; Persistent Data Explorer.
    /// </summary>
    public partial class PersistentDataExplorerWindow : EditorWindow
    {
        private const float RowHeight = 20f;

        private static readonly Color SelectionColor = new Color(0.24f, 0.48f, 0.90f, 0.45f);
        private static readonly Color StripeColor = new Color(0f, 0f, 0f, 0.06f);
        private static readonly Color SeparatorColor = new Color(0f, 0f, 0f, 0.3f);

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".json", ".txt", ".xml", ".csv", ".log", ".ini", ".cfg", ".yaml", ".yml", ".md",
        };

        [SerializeField] private string currentDir;
        [SerializeField] private string selectedPath;
        [SerializeField] private string search = "";

        // What's typed in the search field; copied to `search` via Defer so the path bar and list
        // never switch between browse/search layouts in the middle of an IMGUI pass.
        private string searchInput = "";

        private string rootPath;
        private string rootSummary = "";
        private readonly List<Entry> entries = new();
        private Vector2 listScroll;

        private GUIContent folderIcon;
        private GUIContent fileIcon;
        private GUIContent textIcon;
        private GUIStyle nameLabel;
        private GUIStyle rightMiniLabel;
        private GUIStyle detailsPadding;

        private struct Entry
        {
            public string FullPath;
            public string DisplayName;
            public bool IsDirectory;
            public long Size;
            public DateTime LastWrite;
        }

        private bool IsSearching => !string.IsNullOrWhiteSpace(search);

        [MenuItem("Tools/Thinhnv/Persistent Data Explorer")]
        public static void Open()
        {
            GetWindow<PersistentDataExplorerWindow>("Persistent Data").minSize = new Vector2(680, 360);
        }

        private void OnEnable()
        {
            folderIcon = EditorGUIUtility.IconContent("Folder Icon");
            fileIcon = EditorGUIUtility.IconContent("DefaultAsset Icon");
            textIcon = EditorGUIUtility.IconContent("TextAsset Icon");
            saveChangesMessage = "The file open in Persistent Data Explorer has unsaved edits. Save them before closing?";
            searchInput = search;

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            Refresh();
            RestorePreviewAfterReload();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        }

        private void OnFocus()
        {
            Refresh();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            Refresh();
        }

        public override void SaveChanges()
        {
            if (SavePreview())
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

        private void Refresh()
        {
            rootPath = NormalizePath(Application.persistentDataPath);
            TryFileOp("create the persistentDataPath folder", () => Directory.CreateDirectory(rootPath));

            if (string.IsNullOrEmpty(currentDir) || !Directory.Exists(currentDir) || !IsSameOrInside(currentDir, rootPath))
            {
                currentDir = rootPath;
            }

            bool keepMissingSelection = previewDirty && selectedPath == previewPath;
            if (!string.IsNullOrEmpty(selectedPath) && !PathExists(selectedPath) && !keepMissingSelection)
            {
                selectedPath = null;
            }

            RebuildEntries();
            RefreshPreviewFromDisk();
            UpdateSelectionInfo();
            UpdateRootSummary();
            Repaint();
        }

        private void RebuildEntries()
        {
            entries.Clear();
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                return;
            }

            bool searching = IsSearching;
            string term = search.Trim();

            try
            {
                IEnumerable<string> paths = searching
                    ? Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.AllDirectories)
                    : Directory.EnumerateFileSystemEntries(currentDir);

                foreach (string raw in paths)
                {
                    string path = NormalizePath(raw);
                    string name = Path.GetFileName(path);
                    if (searching && name.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    bool isDir = Directory.Exists(path);
                    entries.Add(new Entry
                    {
                        FullPath = path,
                        DisplayName = searching ? RelativePath(path) : name,
                        IsDirectory = isDir,
                        Size = isDir ? CountChildren(path) : new FileInfo(path).Length,
                        LastWrite = isDir ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path),
                    });
                }
            }
            catch (Exception e) when (IsIoException(e))
            {
                Debug.LogWarning($"Persistent Data Explorer: could not list '{ToNativePath(currentDir)}': {e.Message}");
            }

            entries.Sort((a, b) => a.IsDirectory != b.IsDirectory
                ? (a.IsDirectory ? -1 : 1)
                : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateRootSummary()
        {
            rootSummary = "";
            try
            {
                int fileCount = 0;
                long totalBytes = 0;
                foreach (string file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    totalBytes += new FileInfo(file).Length;
                }

                rootSummary = $"{fileCount} file(s) · {FormatBytes(totalBytes)}";
            }
            catch (Exception e) when (IsIoException(e))
            {
                rootSummary = "";
            }
        }

        private void OnGUI()
        {
            nameLabel ??= new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };
            rightMiniLabel ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            detailsPadding ??= new GUIStyle { padding = new RectOffset(6, 6, 4, 4) };

            DrawToolbar();
            DrawPathBar();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(position.width * 0.45f)))
                {
                    DrawList();
                }

                Rect separator = GUILayoutUtility.GetRect(1, 1, GUILayout.Width(1), GUILayout.ExpandHeight(true));
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(separator, SeparatorColor);
                }

                using (new EditorGUILayout.VerticalScope(detailsPadding))
                {
                    DrawDetails();
                }
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(56)))
                {
                    Defer(Refresh);
                }

                using (new EditorGUI.DisabledScope(IsSearching || currentDir == rootPath))
                {
                    if (GUILayout.Button("Up", EditorStyles.toolbarButton, GUILayout.Width(32)))
                    {
                        string parent = NormalizePath(Path.GetDirectoryName(currentDir));
                        Defer(() => NavigateTo(parent));
                    }
                }

                if (GUILayout.Button("New Folder", EditorStyles.toolbarButton, GUILayout.Width(74)))
                {
                    Defer(CreateFolder);
                }

                if (GUILayout.Button("New File", EditorStyles.toolbarButton, GUILayout.Width(62)))
                {
                    Defer(CreateFile);
                }

                if (GUILayout.Button("Import...", EditorStyles.toolbarButton, GUILayout.Width(64)))
                {
                    Defer(ImportFromDialog);
                }

                GUILayout.FlexibleSpace();

                EditorGUI.BeginChangeCheck();
                searchInput = GUILayout.TextField(searchInput ?? "", EditorStyles.toolbarSearchField, GUILayout.Width(150));
                if (EditorGUI.EndChangeCheck())
                {
                    string term = searchInput;
                    Defer(() =>
                    {
                        search = term;
                        RebuildEntries();
                    });
                }

                if (GUILayout.Button(RevealLabel, EditorStyles.toolbarButton, GUILayout.Width(110)))
                {
                    string dir = currentDir;
                    Defer(() => OpenFolderInOS(dir));
                }

                Color previous = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                if (GUILayout.Button("Clear All", EditorStyles.toolbarButton, GUILayout.Width(64)))
                {
                    Defer(ClearAll);
                }

                GUI.backgroundColor = previous;
            }
        }

        private void DrawPathBar()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (IsSearching)
                {
                    GUILayout.Label($"Search results across persistentDataPath ({entries.Count})", EditorStyles.miniBoldLabel);
                }
                else
                {
                    if (GUILayout.Button("persistentDataPath", EditorStyles.miniButton))
                    {
                        Defer(() => NavigateTo(rootPath));
                    }

                    string relative = RelativePath(currentDir);
                    if (relative.Length > 0)
                    {
                        string accumulated = rootPath;
                        foreach (string part in relative.Split('/'))
                        {
                            accumulated = accumulated + "/" + part;
                            string target = accumulated;
                            GUILayout.Label("›", GUILayout.Width(10));
                            if (GUILayout.Button(part, EditorStyles.miniButton))
                            {
                                Defer(() => NavigateTo(target));
                            }
                        }
                    }
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label(rootSummary, EditorStyles.miniLabel);

                if (GUILayout.Button("Copy Path", EditorStyles.miniButton, GUILayout.Width(70)))
                {
                    EditorGUIUtility.systemCopyBuffer = ToNativePath(currentDir);
                }
            }

            EditorGUILayout.SelectableLabel(ToNativePath(currentDir), EditorStyles.miniLabel, GUILayout.Height(16));
        }

        private void DrawList()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Name", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label("Size", EditorStyles.miniLabel, GUILayout.Width(70));
                GUILayout.Label("Modified", EditorStyles.miniLabel, GUILayout.Width(100));
            }

            listScroll = EditorGUILayout.BeginScrollView(listScroll);

            if (entries.Count == 0)
            {
                GUILayout.Label(
                    IsSearching ? "No matches." : "This folder is empty.\nDrag files or folders here to import them.",
                    EditorStyles.centeredGreyMiniLabel);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                DrawRow(entries[i], i);
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.EndScrollView();

            Rect listRect = GUILayoutUtility.GetLastRect();
            HandleDragAndDrop(listRect);
            HandleBackgroundContextClick(listRect);
        }

        private void DrawRow(Entry entry, int index)
        {
            Rect rect = GUILayoutUtility.GetRect(0, RowHeight, GUILayout.ExpandWidth(true));
            Event e = Event.current;

            if (e.type == EventType.Repaint)
            {
                if (entry.FullPath == selectedPath)
                {
                    EditorGUI.DrawRect(rect, SelectionColor);
                }
                else if (index % 2 == 1)
                {
                    EditorGUI.DrawRect(rect, StripeColor);
                }

                Texture icon = IconForPath(entry.FullPath, entry.IsDirectory).image;
                if (icon != null)
                {
                    GUI.DrawTexture(new Rect(rect.x + 4, rect.y + 2, 16, 16), icon, ScaleMode.ScaleToFit);
                }
            }

            var dateRect = new Rect(rect.xMax - 100, rect.y, 100, rect.height);
            var sizeRect = new Rect(dateRect.x - 74, rect.y, 70, rect.height);
            var nameRect = new Rect(rect.x + 24, rect.y, sizeRect.x - rect.x - 28, rect.height);

            GUI.Label(nameRect, entry.DisplayName, nameLabel);
            GUI.Label(sizeRect, entry.IsDirectory ? $"{entry.Size} item(s)" : FormatBytes(entry.Size), rightMiniLabel);
            GUI.Label(dateRect, entry.LastWrite.ToString("yyyy-MM-dd HH:mm"), rightMiniLabel);

            if (e.type != EventType.MouseDown || !rect.Contains(e.mousePosition))
            {
                return;
            }

            string path = entry.FullPath;
            bool isDir = entry.IsDirectory;

            if (e.button == 0)
            {
                if (e.clickCount == 2)
                {
                    Defer(() => OpenEntry(path, isDir));
                }
                else
                {
                    Defer(() => Select(path));
                }

                e.Use();
            }
            else if (e.button == 1)
            {
                Defer(() => Select(path));
                ShowEntryContextMenu(path, isDir);
                e.Use();
            }
        }

        private void ShowEntryContextMenu(string path, bool isDir)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(isDir ? "Open Folder" : "Open With Default App"), false, () => Defer(() => OpenEntry(path, isDir)));
            menu.AddItem(new GUIContent(RevealLabel), false, () => EditorUtility.RevealInFinder(path));
            menu.AddItem(new GUIContent("Copy Path"), false, () => EditorGUIUtility.systemCopyBuffer = ToNativePath(path));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Duplicate"), false, () => Defer(() => Duplicate(path)));
            menu.AddItem(new GUIContent("Export..."), false, () => Defer(() => Export(path)));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete"), false, () => Defer(() => Delete(path)));
            menu.ShowAsContext();
        }

        private void HandleBackgroundContextClick(Rect listRect)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 1 || !listRect.Contains(e.mousePosition))
            {
                return;
            }

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("New Folder"), false, () => Defer(CreateFolder));
            menu.AddItem(new GUIContent("New File"), false, () => Defer(CreateFile));
            menu.AddItem(new GUIContent("Import..."), false, () => Defer(ImportFromDialog));
            menu.AddSeparator("");
            string dir = currentDir;
            menu.AddItem(new GUIContent(RevealLabel), false, () => OpenFolderInOS(dir));
            menu.ShowAsContext();
            e.Use();
        }

        private void HandleDragAndDrop(Rect dropArea)
        {
            Event e = Event.current;
            if ((e.type != EventType.DragUpdated && e.type != EventType.DragPerform) || !dropArea.Contains(e.mousePosition))
            {
                return;
            }

            string[] paths = DragAndDrop.paths;
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            DragAndDrop.visualMode = IsSearching ? DragAndDropVisualMode.Rejected : DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragPerform && !IsSearching)
            {
                DragAndDrop.AcceptDrag();
                var dropped = (string[])paths.Clone();
                Defer(() => ImportPaths(dropped));
            }

            e.Use();
        }

        private static string RevealLabel =>
            Application.platform == RuntimePlatform.OSXEditor ? "Reveal in Finder" : "Show in Explorer";
    }
}
