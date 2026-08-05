using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.Fbx
{
    /// <summary>
    /// Phase 3b: draws the selected node's mesh as a wireframe in the SceneView
    /// with a pickable handle per control point, plus a PositionHandle for
    /// dragging the selected one.
    ///
    /// Deliberately ignores the node's actual world transform and any
    /// FBX-to-Unity axis/unit conversion — control points are drawn using their
    /// raw mesh-space coordinates directly as SceneView world coordinates. This
    /// keeps the preview internally consistent for shaping a single mesh, but
    /// it will not line up with where an imported copy of the same file
    /// appears elsewhere in the scene (Unity's importer applies axis/unit
    /// conversion this tool intentionally does not, to avoid touching the
    /// underlying node/scene transform).
    /// </summary>
    public class FbxVertexSceneGUI
    {
        private const int MaxPointsDrawn = 500;

        private readonly FbxVertexEditSession _session;
        private bool _enabled;

        public FbxVertexSceneGUI(FbxVertexEditSession session)
        {
            _session = session;
        }

        public void Enable()
        {
            if (_enabled)
            {
                return;
            }

            SceneView.duringSceneGui += OnSceneGUI;
            _enabled = true;
        }

        public void Disable()
        {
            if (!_enabled)
            {
                return;
            }

            SceneView.duringSceneGui -= OnSceneGUI;
            _enabled = false;
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            var mesh = _session.Mesh;
            if (mesh == null)
            {
                return;
            }

            DrawWireframe(mesh);
            DrawControlPointHandles(mesh);

            if (_session.SelectedIndex >= 0 && _session.SelectedIndex < mesh.GetControlPointsCount())
            {
                DrawPositionHandle(mesh);
            }
        }

        private static void DrawWireframe(FbxMesh mesh)
        {
            Handles.color = Color.cyan;
            var polygonCount = mesh.GetPolygonCount();
            for (var p = 0; p < polygonCount; p++)
            {
                var size = mesh.GetPolygonSize(p);
                if (size < 2)
                {
                    continue;
                }

                for (var c = 0; c < size; c++)
                {
                    var a = ToVector3(mesh.GetControlPointAt(mesh.GetPolygonVertex(p, c)));
                    var b = ToVector3(mesh.GetControlPointAt(mesh.GetPolygonVertex(p, (c + 1) % size)));
                    Handles.DrawLine(a, b);
                }
            }
        }

        private void DrawControlPointHandles(FbxMesh mesh)
        {
            var count = Mathf.Min(mesh.GetControlPointsCount(), MaxPointsDrawn);
            for (var i = 0; i < count; i++)
            {
                var worldPos = ToVector3(mesh.GetControlPointAt(i));
                var size = HandleUtility.GetHandleSize(worldPos) * 0.05f;
                Handles.color = i == _session.SelectedIndex ? Color.yellow : Color.white;

                if (Handles.Button(worldPos, Quaternion.identity, size, size, Handles.SphereHandleCap))
                {
                    _session.SelectedIndex = i;
                }
            }
        }

        private void DrawPositionHandle(FbxMesh mesh)
        {
            var index = _session.SelectedIndex;
            var worldPos = ToVector3(mesh.GetControlPointAt(index));

            EditorGUI.BeginChangeCheck();
            var newPos = Handles.PositionHandle(worldPos, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                _session.MoveSelected(newPos);
            }
        }

        private static Vector3 ToVector3(FbxVector4 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    }
}
