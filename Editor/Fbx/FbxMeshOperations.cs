using System;
using System.Collections.Generic;
using Autodesk.Fbx;

namespace Percas.UnityTools.Fbx
{
    public readonly struct FbxWeldResult
    {
        public readonly int NewControlPointCount;
        public readonly int DroppedPolygonCount;

        public FbxWeldResult(int newControlPointCount, int droppedPolygonCount)
        {
            NewControlPointCount = newControlPointCount;
            DroppedPolygonCount = droppedPolygonCount;
        }
    }

    /// <summary>
    /// Phase 3a: control-point edits, vertex welding and normal recalculation.
    /// Scoped to non-skinned static meshes — FbxDocument blocks Save entirely
    /// for files with skinning/animation/blend shapes (see FbxDocument.IsSaveBlocked),
    /// and callers should also check FbxNodeOperations.IsBoneOrSkinned per-node
    /// before offering these operations in the UI.
    /// </summary>
    public static class FbxMeshOperations
    {
        public static bool TryGetMesh(FbxNode node, out FbxMesh mesh)
        {
            // node.GetMesh() is the SDK's safe accessor (null if not a mesh) —
            // casting GetNodeAttribute() directly to FbxMesh throws in this binding.
            mesh = node != null ? node.GetMesh() : null;
            return mesh != null;
        }

        public static void SetControlPoint(FbxMesh mesh, int index, FbxVector4 value)
        {
            mesh.SetControlPointAt(value, index);
        }

        /// <summary>
        /// Midpoint of the mesh's control-point bounding box, in the mesh's own
        /// local space. This is a property of the raw vertex data, not the same
        /// thing as the node's RotationPivot/ScalingPivot/GeometricTranslation —
        /// a mesh authored with its data already centered on its pivot will have
        /// bounds center ≈ (0,0,0) with no pivot property set at all.
        /// </summary>
        public static FbxVector4? ComputeBoundsCenter(FbxMesh mesh)
        {
            var count = mesh.GetControlPointsCount();
            if (count == 0)
            {
                return null;
            }

            var min = mesh.GetControlPointAt(0);
            var max = min;
            for (var i = 1; i < count; i++)
            {
                var p = mesh.GetControlPointAt(i);
                min = new FbxVector4(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
                max = new FbxVector4(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
            }

            return new FbxVector4((min.X + max.X) / 2.0, (min.Y + max.Y) / 2.0, (min.Z + max.Z) / 2.0);
        }

        /// <summary>
        /// Shifts every control point so the mesh's local bounds center becomes
        /// newCenterLocal, and compensates the node's local translation (by the
        /// same shift rotated/scaled through the node's own LclRotation/LclScaling)
        /// so the object does not visually jump in the scene — the classic
        /// "recenter pivot" operation, just driven by typing a target center
        /// instead of a one-click "move to origin" button.
        ///
        /// Ignores rotation/scaling pivots and pre/post-rotation on this node —
        /// the same approximation as FbxNodeOperations.Reparent's preserve-world-
        /// transform path, for the same reason (no animation evaluator in this binding).
        /// </summary>
        public static void RecenterPivot(FbxNode node, FbxVector4 newCenterLocal)
        {
            if (!TryGetMesh(node, out var mesh))
            {
                throw new InvalidOperationException("Node has no mesh.");
            }

            var currentCenter = ComputeBoundsCenter(mesh);
            if (!currentCenter.HasValue)
            {
                throw new InvalidOperationException("Mesh has no control points.");
            }

            var delta = new FbxVector4(
                newCenterLocal.X - currentCenter.Value.X,
                newCenterLocal.Y - currentCenter.Value.Y,
                newCenterLocal.Z - currentCenter.Value.Z);

            var count = mesh.GetControlPointsCount();
            for (var i = 0; i < count; i++)
            {
                var p = mesh.GetControlPointAt(i);
                mesh.SetControlPointAt(new FbxVector4(p.X + delta.X, p.Y + delta.Y, p.Z + delta.Z), i);
            }

            var rotateScale = new FbxAMatrix();
            rotateScale.SetTRS(
                new FbxVector4(0, 0, 0),
                ToVector4(FbxNodeOperations.GetLocalRotation(node)),
                ToVector4(FbxNodeOperations.GetLocalScaling(node)));

            // MultR transforms a direction (rotate + scale only, no translation) —
            // MultT would also add the matrix's translation, which we don't want here.
            var rotatedDelta = rotateScale.MultR(delta);

            var oldTranslation = FbxNodeOperations.GetLocalTranslation(node);
            var newTranslation = new FbxDouble3(
                oldTranslation.X - rotatedDelta.X,
                oldTranslation.Y - rotatedDelta.Y,
                oldTranslation.Z - rotatedDelta.Z);

            FbxNodeOperations.SetLocalTranslation(node, newTranslation);
        }

        private static FbxVector4 ToVector4(FbxDouble3 v) => new FbxVector4(v.X, v.Y, v.Z);

        /// <summary>
        /// Rebuilds a fresh per-control-point normal layer from face geometry
        /// (cross-product face normal, accumulated into every corner it touches
        /// before normalizing — larger faces naturally contribute more, the same
        /// effect Unity's Mesh.RecalculateNormals has). This always replaces
        /// whatever normal layer existed before; there is no partial update.
        /// </summary>
        public static void RecalculateNormals(FbxMesh mesh)
        {
            var pointCount = mesh.GetControlPointsCount();
            var accum = new FbxVector4[pointCount];
            for (var i = 0; i < pointCount; i++)
            {
                accum[i] = new FbxVector4(0, 0, 0);
            }

            var polygonCount = mesh.GetPolygonCount();
            for (var p = 0; p < polygonCount; p++)
            {
                var size = mesh.GetPolygonSize(p);
                if (size < 3)
                {
                    continue;
                }

                var i0 = mesh.GetPolygonVertex(p, 0);
                var i1 = mesh.GetPolygonVertex(p, 1);
                var i2 = mesh.GetPolygonVertex(p, 2);

                var faceNormal = Cross(
                    Subtract(mesh.GetControlPointAt(i1), mesh.GetControlPointAt(i0)),
                    Subtract(mesh.GetControlPointAt(i2), mesh.GetControlPointAt(i0)));

                for (var c = 0; c < size; c++)
                {
                    var cp = mesh.GetPolygonVertex(p, c);
                    accum[cp] = Add(accum[cp], faceNormal);
                }
            }

            var layer = mesh.GetLayer(0);
            if (layer == null)
            {
                mesh.CreateLayer();
                layer = mesh.GetLayer(0);
            }

            var element = FbxLayerElementNormal.Create(mesh, "Normals");
            element.SetMappingMode(FbxLayerElement.EMappingMode.eByControlPoint);
            element.SetReferenceMode(FbxLayerElement.EReferenceMode.eDirect);

            var directArray = element.GetDirectArray();
            for (var i = 0; i < pointCount; i++)
            {
                directArray.Add(Normalize(accum[i]));
            }

            layer.SetNormals(element);
        }

        /// <summary>
        /// Merges control points within <paramref name="threshold"/> of each
        /// other and rebuilds polygon topology to match. FbxMesh topology is
        /// append-only (Begin/AddPolygon/EndPolygon), it cannot be edited in
        /// place, so this builds a brand new FbxMesh and swaps it onto the node
        /// rather than mutating the existing one.
        ///
        /// Scope limitation (v1): UV and per-polygon material layers are not
        /// preserved through a weld — the merged geometry gets a single default
        /// material index and no UV set. Reassign the material via the Material
        /// tab and normals are recalculated automatically after welding.
        /// Grouping is O(n^2); fine for modest meshes, avoid on dense ones.
        /// </summary>
        public static FbxWeldResult Weld(FbxScene scene, FbxNode node, double threshold)
        {
            if (!TryGetMesh(node, out var oldMesh))
            {
                throw new InvalidOperationException("Node has no mesh.");
            }

            var oldCount = oldMesh.GetControlPointsCount();
            var oldPoints = new FbxVector4[oldCount];
            for (var i = 0; i < oldCount; i++)
            {
                oldPoints[i] = oldMesh.GetControlPointAt(i);
            }

            var remap = new int[oldCount];
            var groupPositions = new List<FbxVector4>();
            for (var i = 0; i < oldCount; i++)
            {
                var found = -1;
                for (var g = 0; g < groupPositions.Count; g++)
                {
                    if (Distance(oldPoints[i], groupPositions[g]) <= threshold)
                    {
                        found = g;
                        break;
                    }
                }

                if (found < 0)
                {
                    groupPositions.Add(oldPoints[i]);
                    found = groupPositions.Count - 1;
                }

                remap[i] = found;
            }

            var newMesh = FbxMesh.Create(scene, oldMesh.GetName() + "_welded");
            newMesh.InitControlPoints(groupPositions.Count);
            for (var i = 0; i < groupPositions.Count; i++)
            {
                newMesh.SetControlPointAt(groupPositions[i], i);
            }

            newMesh.CreateLayer();
            var layer = newMesh.GetLayer(0);
            var materialElement = FbxLayerElementMaterial.Create(newMesh, "Material");
            materialElement.SetMappingMode(FbxLayerElement.EMappingMode.eAllSame);
            materialElement.SetReferenceMode(FbxLayerElement.EReferenceMode.eIndexToDirect);
            materialElement.GetIndexArray().Add(0);
            layer.SetMaterials(materialElement);

            var polygonCount = oldMesh.GetPolygonCount();
            var droppedPolygons = 0;

            for (var p = 0; p < polygonCount; p++)
            {
                var size = oldMesh.GetPolygonSize(p);
                var corners = new List<int>();
                for (var c = 0; c < size; c++)
                {
                    var newCp = remap[oldMesh.GetPolygonVertex(p, c)];
                    if (corners.Count == 0 || corners[corners.Count - 1] != newCp)
                    {
                        corners.Add(newCp);
                    }
                }

                if (corners.Count >= 2 && corners[0] == corners[corners.Count - 1])
                {
                    corners.RemoveAt(corners.Count - 1);
                }

                if (corners.Count < 3)
                {
                    droppedPolygons++;
                    continue;
                }

                newMesh.BeginPolygon();
                foreach (var cp in corners)
                {
                    newMesh.AddPolygon(cp);
                }
                newMesh.EndPolygon();
            }

            node.SetNodeAttribute(newMesh);
            oldMesh.Destroy();

            RecalculateNormals(newMesh);

            return new FbxWeldResult(groupPositions.Count, droppedPolygons);
        }

        private static double Distance(FbxVector4 a, FbxVector4 b)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static FbxVector4 Cross(FbxVector4 a, FbxVector4 b) => new FbxVector4(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        private static FbxVector4 Subtract(FbxVector4 a, FbxVector4 b) => new FbxVector4(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        private static FbxVector4 Add(FbxVector4 a, FbxVector4 b) => new FbxVector4(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        private static FbxVector4 Normalize(FbxVector4 v)
        {
            var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return length > 1e-8 ? new FbxVector4(v.X / length, v.Y / length, v.Z / length) : new FbxVector4(0, 0, 1);
        }
    }
}
