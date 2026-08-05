using System;
using System.IO;
using Autodesk.Fbx;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace Thinhnv.UnityTools.Fbx
{
    /// <summary>
    /// FBX editor: browse a .fbx file's node hierarchy and edit
    /// transform/pivot/name/parenting (Phase 1), material/texture assignment
    /// (Phase 2) and mesh control points via table or SceneView (Phase 3a/3b), saving the changes back
    /// into the original file (with an automatic backup) via FbxDocument.
    /// </summary>
    public class FbxEditorWindow : EditorWindow
    {
        private enum Tab
        {
            Transform,
            Material,
            Mesh
        }

        [MenuItem("Thinhnv/Fbx Tools/Fbx Editor")]
        private static void ShowWindow()
        {
            GetWindow<FbxEditorWindow>("Fbx Editor");
        }

        private FbxDocument _document;
        private FbxNodeTreeView _treeView;
        private TreeViewState _treeViewState;
        private FbxTransformPanel _transformPanel;
        private FbxMaterialPanel _materialPanel;
        private FbxMeshPanel _meshPanel;
        private Tab _activeTab;
        private Vector2 _rightPaneScroll;

        private void OnEnable()
        {
            _document = new FbxDocument();
            _treeViewState = new TreeViewState();
            _treeView = new FbxNodeTreeView(_treeViewState, _document);
            _transformPanel = new FbxTransformPanel(_document);
            _materialPanel = new FbxMaterialPanel(_document);
            _meshPanel = new FbxMeshPanel(_document);
        }

        private void OnDisable()
        {
            _meshPanel?.Dispose();
            _document?.Close();
        }

        private void OnGUI()
        {
            HandleFileDrop();
            DrawToolbar();

            if (!_document.IsOpen)
            {
                EditorGUILayout.HelpBox(
                    "Drag an .fbx file here (from Windows Explorer or the Project window) or use Open...",
                    MessageType.Info);
            }
            else if (_document.IsSaveBlocked)
            {
                EditorGUILayout.HelpBox(
                    "This file contains animation, skinning or blend shapes. Saving is disabled until " +
                    "the tool supports round-tripping that data safely.", MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(position.width * 0.45f), GUILayout.ExpandHeight(true));
            var treeRect = GUILayoutUtility.GetRect(0, 100000, 0, 100000);
            _treeView.OnGUI(treeRect);
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical();
            _activeTab = (Tab)GUILayout.Toolbar((int)_activeTab, new[] { "Transform", "Material", "Mesh" });

            _rightPaneScroll = EditorGUILayout.BeginScrollView(_rightPaneScroll);
            var selection = _treeView.GetSelection();
            var selectedNode = selection.Count > 0 ? _treeView.GetNode(selection[0]) : null;

            switch (_activeTab)
            {
                case Tab.Material:
                    _materialPanel.Draw(selectedNode);
                    break;
                case Tab.Mesh:
                    _meshPanel.Draw(selectedNode);
                    break;
                default:
                    _transformPanel.Draw(selectedNode);
                    break;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Open...", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                OpenFile();
            }

            using (new EditorGUI.DisabledScope(!_document.IsOpen))
            {
                if (GUILayout.Button("Reload", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    ReloadFile();
                }

                using (new EditorGUI.DisabledScope(!_document.IsDirty || _document.IsSaveBlocked))
                {
                    if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    {
                        SaveFile(_document.FilePath);
                    }
                }

                if (GUILayout.Button("Save As...", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    var path = EditorUtility.SaveFilePanel("Save FBX As", "", "", "fbx");
                    if (!string.IsNullOrEmpty(path))
                    {
                        SaveFile(path);
                    }
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(_document.IsOpen ? _document.FilePath : "(no file open)");

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Accepts an .fbx dragged in either as a raw OS file (DragAndDrop.paths,
        /// e.g. from Windows Explorer) or as a Unity asset reference
        /// (DragAndDrop.objectReferences, e.g. from the Project window).
        /// </summary>
        private void HandleFileDrop()
        {
            var evt = Event.current;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform)
            {
                return;
            }

            var fbxPath = FindDraggedFbxPath();
            if (fbxPath == null)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                OpenFileAtPath(fbxPath);
            }

            evt.Use();
        }

        private static string FindDraggedFbxPath()
        {
            foreach (var path in DragAndDrop.paths)
            {
                if (IsFbxPath(path))
                {
                    return path;
                }
            }

            foreach (var obj in DragAndDrop.objectReferences)
            {
                var assetPath = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(assetPath) && IsFbxPath(assetPath))
                {
                    return Path.GetFullPath(assetPath);
                }
            }

            return null;
        }

        private static bool IsFbxPath(string path) =>
            string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase);

        private void OpenFile()
        {
            var path = EditorUtility.OpenFilePanel("Open FBX", "", "fbx");
            if (!string.IsNullOrEmpty(path))
            {
                OpenFileAtPath(path);
            }
        }

        private void OpenFileAtPath(string path)
        {
            try
            {
                _document.Open(path);
                _treeView.Reload();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Failed to open FBX", e.Message, "OK");
            }
        }

        private void ReloadFile()
        {
            if (_document.IsDirty && !EditorUtility.DisplayDialog("Reload FBX",
                    "There are unsaved changes. Reloading will discard them. Continue?", "Reload", "Cancel"))
            {
                return;
            }

            OpenFileAtPath(_document.FilePath);
        }

        private void SaveFile(string path)
        {
            if (!_document.ChangeLog.IsEmpty &&
                !EditorUtility.DisplayDialog("Confirm changes", _document.ChangeLog.BuildSummary(), "Save", "Cancel"))
            {
                return;
            }

            try
            {
                var backupPath = _document.Save(path);
                var message = backupPath != null
                    ? $"Saved. Original file backed up to:\n{backupPath}"
                    : "Saved.";
                EditorUtility.DisplayDialog("Fbx Editor", message, "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Failed to save FBX", e.Message, "OK");
            }
        }
    }
}
