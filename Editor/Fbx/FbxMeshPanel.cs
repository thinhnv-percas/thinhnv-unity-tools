using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;

namespace Percas.UnityTools.Fbx
{
    /// <summary>
    /// Mesh inspector tab: a data-table view of control points (Phase 3a) plus
    /// an optional SceneView gizmo mode (Phase 3b) for dragging one control
    /// point at a time, and Weld / Recalculate Normals actions.
    /// </summary>
    public class FbxMeshPanel
    {
        private const int MaxControlPointsShown = 200;

        private readonly FbxDocument _document;
        private readonly FbxVertexEditSession _vertexEditSession;
        private readonly FbxVertexSceneGUI _vertexSceneGui;
        private float _weldThreshold = 0.0001f;
        private bool _sceneEditEnabled;
        private FbxNode _lastNode;

        public FbxMeshPanel(FbxDocument document)
        {
            _document = document;
            _vertexEditSession = new FbxVertexEditSession(document);
            _vertexSceneGui = new FbxVertexSceneGUI(_vertexEditSession);
        }

        public void Dispose()
        {
            _vertexSceneGui.Disable();
        }

        public void Draw(FbxNode node)
        {
            if (node == null)
            {
                EditorGUILayout.HelpBox("Select a node to edit its mesh.", MessageType.Info);
                DisableSceneEdit();
                return;
            }

            if (!FbxMeshOperations.TryGetMesh(node, out var mesh))
            {
                EditorGUILayout.HelpBox("Selected node has no mesh.", MessageType.Info);
                DisableSceneEdit();
                return;
            }

            if (FbxNodeOperations.IsBoneOrSkinned(node))
            {
                EditorGUILayout.HelpBox(
                    "This mesh is skinned. Vertex editing is disabled for skinned meshes in this phase.",
                    MessageType.Warning);
                DisableSceneEdit();
                return;
            }

            if (node != _lastNode)
            {
                _vertexEditSession.SetTarget(node, mesh);
                _lastNode = node;
            }

            EditorGUILayout.LabelField($"{mesh.GetControlPointsCount()} control points, {mesh.GetPolygonCount()} polygons");

            EditorGUILayout.BeginHorizontal();
            _weldThreshold = EditorGUILayout.FloatField("Weld Threshold", _weldThreshold);
            var weldClicked = GUILayout.Button("Weld", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            if (weldClicked)
            {
                RunWeld(node, mesh);
                return; // node's mesh attribute was replaced; bail out of this draw pass.
            }

            if (GUILayout.Button("Recalculate Normals"))
            {
                FbxMeshOperations.RecalculateNormals(mesh);
                _document.RecordChange(FbxChangeKind.MeshChanged, FbxNodeOperations.GetNodePath(node), "normals", "recalculated");
            }

            EditorGUILayout.Space();
            DrawSceneEditSection();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Control Points", EditorStyles.boldLabel);
            DrawControlPoints(node, mesh);
        }

        private void DrawSceneEditSection()
        {
            EditorGUILayout.LabelField("Scene View Editing", EditorStyles.boldLabel);

            var newToggle = EditorGUILayout.Toggle("Edit In Scene View", _sceneEditEnabled);
            if (newToggle != _sceneEditEnabled)
            {
                _sceneEditEnabled = newToggle;
                if (_sceneEditEnabled)
                {
                    _vertexSceneGui.Enable();
                }
                else
                {
                    _vertexSceneGui.Disable();
                }
            }

            if (!_sceneEditEnabled)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "Click a point in the SceneView to select it, drag its handle to move it. " +
                "The preview is drawn in the mesh's raw local space, not the node's actual " +
                "position/orientation in the hierarchy.", MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(!_vertexEditSession.CanUndo))
            {
                if (GUILayout.Button("Undo Vertex Edit"))
                {
                    _vertexEditSession.Undo();
                    SceneView.RepaintAll();
                }
            }
            using (new EditorGUI.DisabledScope(!_vertexEditSession.CanRedo))
            {
                if (GUILayout.Button("Redo Vertex Edit"))
                {
                    _vertexEditSession.Redo();
                    SceneView.RepaintAll();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_vertexEditSession.SelectedIndex >= 0)
            {
                EditorGUILayout.LabelField($"Selected control point: #{_vertexEditSession.SelectedIndex}");
            }
        }

        private void DisableSceneEdit()
        {
            if (_sceneEditEnabled)
            {
                _sceneEditEnabled = false;
                _vertexSceneGui.Disable();
            }
        }

        private void DrawControlPoints(FbxNode node, FbxMesh mesh)
        {
            var count = mesh.GetControlPointsCount();
            var shown = Mathf.Min(count, MaxControlPointsShown);
            if (count > shown)
            {
                EditorGUILayout.HelpBox($"Showing first {shown} of {count} control points.", MessageType.Info);
            }

            for (var i = 0; i < shown; i++)
            {
                var point = mesh.GetControlPointAt(i);
                var vec = new Vector3((float)point.X, (float)point.Y, (float)point.Z);

                EditorGUI.BeginChangeCheck();
                var newVec = EditorGUILayout.Vector3Field($"#{i}", vec);
                if (EditorGUI.EndChangeCheck())
                {
                    var newValue = new FbxVector4(newVec.x, newVec.y, newVec.z);
                    FbxMeshOperations.SetControlPoint(mesh, i, newValue);
                    _document.RecordChange(
                        FbxChangeKind.MeshChanged,
                        FbxNodeOperations.GetNodePath(node) + $" (control point #{i})",
                        $"({point.X:F3}, {point.Y:F3}, {point.Z:F3})",
                        $"({newVec.x:F3}, {newVec.y:F3}, {newVec.z:F3})");
                    SceneView.RepaintAll();
                }
            }
        }

        private void RunWeld(FbxNode node, FbxMesh mesh)
        {
            var before = mesh.GetControlPointsCount();
            var result = FbxMeshOperations.Weld(_document.Scene, node, _weldThreshold);

            _document.RecordChange(
                FbxChangeKind.MeshChanged,
                FbxNodeOperations.GetNodePath(node),
                $"{before} control points",
                $"{result.NewControlPointCount} control points" +
                (result.DroppedPolygonCount > 0 ? $", {result.DroppedPolygonCount} degenerate polygon(s) dropped" : string.Empty));

            // The old FbxMesh was destroyed by Weld; refresh the session onto the
            // node's new mesh attribute rather than leave it pointing at freed memory.
            if (FbxMeshOperations.TryGetMesh(node, out var newMesh))
            {
                _vertexEditSession.SetTarget(node, newMesh);
            }

            if (result.DroppedPolygonCount > 0)
            {
                EditorUtility.DisplayDialog("Weld",
                    $"{result.DroppedPolygonCount} polygon(s) collapsed to fewer than 3 vertices and were dropped.", "OK");
            }

            SceneView.RepaintAll();
        }
    }
}
