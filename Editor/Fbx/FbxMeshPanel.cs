using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;

namespace Percas.UnityTools.Fbx
{
    /// <summary>
    /// Phase 3a inspector tab: a data-table view of control points (no SceneView
    /// gizmo editing yet — see the plan's Phase 3b) plus Weld and Recalculate
    /// Normals actions.
    /// </summary>
    public class FbxMeshPanel
    {
        private const int MaxControlPointsShown = 200;

        private readonly FbxDocument _document;
        private float _weldThreshold = 0.0001f;

        public FbxMeshPanel(FbxDocument document)
        {
            _document = document;
        }

        public void Draw(FbxNode node)
        {
            if (node == null)
            {
                EditorGUILayout.HelpBox("Select a node to edit its mesh.", MessageType.Info);
                return;
            }

            if (!FbxMeshOperations.TryGetMesh(node, out var mesh))
            {
                EditorGUILayout.HelpBox("Selected node has no mesh.", MessageType.Info);
                return;
            }

            if (FbxNodeOperations.IsBoneOrSkinned(node))
            {
                EditorGUILayout.HelpBox(
                    "This mesh is skinned. Vertex editing is disabled for skinned meshes in this phase.",
                    MessageType.Warning);
                return;
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
            EditorGUILayout.LabelField("Control Points", EditorStyles.boldLabel);
            DrawControlPoints(node, mesh);
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

            if (result.DroppedPolygonCount > 0)
            {
                EditorUtility.DisplayDialog("Weld",
                    $"{result.DroppedPolygonCount} polygon(s) collapsed to fewer than 3 vertices and were dropped.", "OK");
            }
        }
    }
}
