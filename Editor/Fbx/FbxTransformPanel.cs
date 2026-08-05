using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.Fbx
{
    /// <summary>
    /// Right-pane inspector for the node currently selected in FbxNodeTreeView:
    /// local transform, rotation/scaling pivot and geometric translation offset.
    /// </summary>
    public class FbxTransformPanel
    {
        private readonly FbxDocument _document;

        public FbxTransformPanel(FbxDocument document)
        {
            _document = document;
        }

        public void Draw(FbxNode node)
        {
            if (node == null)
            {
                EditorGUILayout.HelpBox("Select a node to edit its transform.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField(FbxNodeOperations.GetNodePath(node), EditorStyles.boldLabel);

            DrawTranslation(node);
            DrawRotation(node);
            DrawScale(node);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pivot", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Rotation/Scaling Pivot and Geometric Offset are separate FBX node properties — " +
                "most files leave them at (0,0,0) unless the source tool explicitly used a moved " +
                "pivot or baked a geometric offset. Mesh Bounds Center below is computed from the " +
                "mesh's own vertex data; editing it moves the mesh's control points and compensates " +
                "the node's Translation so the object stays put in the scene — a \"recenter pivot\" " +
                "operation, not a write to any of the three properties beneath it.", MessageType.Info);

            DrawBoundsCenter(node);
            DrawRotationPivot(node);
            DrawScalingPivot(node);
            DrawGeometricOffset(node);
        }

        private void DrawBoundsCenter(FbxNode node)
        {
            if (!FbxMeshOperations.TryGetMesh(node, out var mesh))
            {
                return;
            }

            var current = FbxMeshOperations.ComputeBoundsCenter(mesh);
            if (!current.HasValue)
            {
                return;
            }

            if (FbxNodeOperations.IsBoneOrSkinned(node))
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Vector3Field("Mesh Bounds Center (read-only, skinned)", ToVector3(current.Value));
                }
                return;
            }

            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Mesh Bounds Center", ToVector3(current.Value));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToVector4(newVec);
                FbxMeshOperations.RecenterPivot(node, newValue);
                RecordChange(FbxChangeKind.MeshChanged, node, "Bounds Center (recentered pivot)",
                    FormatVector4(current.Value), FormatVector4(newValue));
            }
        }

        private void DrawTranslation(FbxNode node)
        {
            var old = FbxNodeOperations.GetLocalTranslation(node);
            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Translation", ToVector3(old));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToDouble3(newVec);
                FbxNodeOperations.SetLocalTranslation(node, newValue);
                RecordChange(FbxChangeKind.TransformChanged, node, "Translation", FormatDouble3(old), FormatDouble3(newValue));
            }
        }

        private void DrawRotation(FbxNode node)
        {
            var old = FbxNodeOperations.GetLocalRotation(node);
            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Rotation", ToVector3(old));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToDouble3(newVec);
                FbxNodeOperations.SetLocalRotation(node, newValue);
                RecordChange(FbxChangeKind.TransformChanged, node, "Rotation", FormatDouble3(old), FormatDouble3(newValue));
            }
        }

        private void DrawScale(FbxNode node)
        {
            var old = FbxNodeOperations.GetLocalScaling(node);
            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Scale", ToVector3(old));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToDouble3(newVec);
                FbxNodeOperations.SetLocalScaling(node, newValue);
                RecordChange(FbxChangeKind.TransformChanged, node, "Scale", FormatDouble3(old), FormatDouble3(newValue));
            }
        }

        private void DrawRotationPivot(FbxNode node)
        {
            var old = FbxNodeOperations.GetRotationPivot(node);
            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Rotation Pivot", ToVector3(old));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToVector4(newVec);
                FbxNodeOperations.SetRotationPivot(node, newValue);
                RecordChange(FbxChangeKind.PivotChanged, node, "Rotation Pivot", FormatVector4(old), FormatVector4(newValue));
            }
        }

        private void DrawScalingPivot(FbxNode node)
        {
            var old = FbxNodeOperations.GetScalingPivot(node);
            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Scaling Pivot", ToVector3(old));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToVector4(newVec);
                FbxNodeOperations.SetScalingPivot(node, newValue);
                RecordChange(FbxChangeKind.PivotChanged, node, "Scaling Pivot", FormatVector4(old), FormatVector4(newValue));
            }
        }

        private void DrawGeometricOffset(FbxNode node)
        {
            var old = FbxNodeOperations.GetGeometricTranslation(node);
            EditorGUI.BeginChangeCheck();
            var newVec = EditorGUILayout.Vector3Field("Geometric Offset", ToVector3(old));
            if (EditorGUI.EndChangeCheck())
            {
                var newValue = ToVector4(newVec);
                FbxNodeOperations.SetGeometricTranslation(node, newValue);
                RecordChange(FbxChangeKind.PivotChanged, node, "Geometric Offset", FormatVector4(old), FormatVector4(newValue));
            }
        }

        private void RecordChange(FbxChangeKind kind, FbxNode node, string fieldName, string oldValue, string newValue)
        {
            var path = FbxNodeOperations.GetNodePath(node) + " (" + fieldName + ")";
            _document.RecordChange(kind, path, oldValue, newValue);
        }

        private static Vector3 ToVector3(FbxDouble3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
        private static Vector3 ToVector3(FbxVector4 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
        private static FbxDouble3 ToDouble3(Vector3 v) => new FbxDouble3(v.x, v.y, v.z);
        private static FbxVector4 ToVector4(Vector3 v) => new FbxVector4(v.x, v.y, v.z);

        private static string FormatDouble3(FbxDouble3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
        private static string FormatVector4(FbxVector4 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
    }
}
