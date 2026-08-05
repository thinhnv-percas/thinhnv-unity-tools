using System.Collections.Generic;
using Autodesk.Fbx;
using UnityEngine;

namespace Thinhnv.UnityTools.Fbx
{
    /// <summary>
    /// Phase 3b: state for one SceneView vertex-editing session on a single
    /// mesh — which control point is selected, and a linear undo/redo stack
    /// for drags. FbxMesh is not a UnityEngine.Object, so Unity's own Undo
    /// system cannot record these edits; this is a minimal replacement scoped
    /// to control-point moves only, separate from FbxDocument.ChangeLog (which
    /// is a running log for the pre-save diff, not an undo stack).
    /// </summary>
    public class FbxVertexEditSession
    {
        private readonly FbxDocument _document;
        private readonly List<(int index, FbxVector4 oldValue, FbxVector4 newValue)> _undoStack =
            new List<(int index, FbxVector4 oldValue, FbxVector4 newValue)>();
        private int _undoPointer;

        public FbxNode Node { get; private set; }
        public FbxMesh Mesh { get; private set; }
        public int SelectedIndex { get; set; } = -1;

        public bool CanUndo => _undoPointer > 0;
        public bool CanRedo => _undoPointer < _undoStack.Count;

        public FbxVertexEditSession(FbxDocument document)
        {
            _document = document;
        }

        public void SetTarget(FbxNode node, FbxMesh mesh)
        {
            Node = node;
            Mesh = mesh;
            SelectedIndex = -1;
            _undoStack.Clear();
            _undoPointer = 0;
        }

        public void MoveSelected(Vector3 newLocalPosition)
        {
            if (Mesh == null || SelectedIndex < 0 || SelectedIndex >= Mesh.GetControlPointsCount())
            {
                return;
            }

            var oldValue = Mesh.GetControlPointAt(SelectedIndex);
            var newValue = new FbxVector4(newLocalPosition.x, newLocalPosition.y, newLocalPosition.z);

            FbxMeshOperations.SetControlPoint(Mesh, SelectedIndex, newValue);
            PushUndo(SelectedIndex, oldValue, newValue);

            _document.RecordChange(
                FbxChangeKind.MeshChanged,
                FbxNodeOperations.GetNodePath(Node) + $" (control point #{SelectedIndex})",
                FormatPoint(oldValue),
                FormatPoint(newValue));
        }

        public void Undo()
        {
            if (!CanUndo)
            {
                return;
            }

            _undoPointer--;
            var entry = _undoStack[_undoPointer];
            FbxMeshOperations.SetControlPoint(Mesh, entry.index, entry.oldValue);
            SelectedIndex = entry.index;
        }

        public void Redo()
        {
            if (!CanRedo)
            {
                return;
            }

            var entry = _undoStack[_undoPointer];
            FbxMeshOperations.SetControlPoint(Mesh, entry.index, entry.newValue);
            SelectedIndex = entry.index;
            _undoPointer++;
        }

        private void PushUndo(int index, FbxVector4 oldValue, FbxVector4 newValue)
        {
            if (_undoPointer < _undoStack.Count)
            {
                _undoStack.RemoveRange(_undoPointer, _undoStack.Count - _undoPointer);
            }

            _undoStack.Add((index, oldValue, newValue));
            _undoPointer = _undoStack.Count;
        }

        private static string FormatPoint(FbxVector4 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";
    }
}
