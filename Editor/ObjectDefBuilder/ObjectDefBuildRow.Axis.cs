using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    public partial class ObjectDefBuildRow
    {
        public GameObject PerAxisModelSource(BuildAxis axis) => axis switch
        {
            BuildAxis.X => modelSourceX,
            BuildAxis.Y => modelSourceY,
            BuildAxis.Z => modelSourceZ,
            BuildAxis.XY => modelSourceXY,
            BuildAxis.YZ => modelSourceYZ,
            BuildAxis.XZ => modelSourceXZ,
            BuildAxis.XYZ => modelSourceXYZ,
            _ => null,
        };

        public void SetPerAxisModelSource(BuildAxis axis, GameObject source)
        {
            switch (axis)
            {
                case BuildAxis.X: modelSourceX = source; break;
                case BuildAxis.Y: modelSourceY = source; break;
                case BuildAxis.Z: modelSourceZ = source; break;
                case BuildAxis.XY: modelSourceXY = source; break;
                case BuildAxis.YZ: modelSourceYZ = source; break;
                case BuildAxis.XZ: modelSourceXZ = source; break;
                case BuildAxis.XYZ: modelSourceXYZ = source; break;
            }
        }

        public bool HasPerAxisModels =>
            modelSourceX != null || modelSourceY != null || modelSourceZ != null ||
            modelSourceXY != null || modelSourceYZ != null || modelSourceXZ != null ||
            modelSourceXYZ != null;

        /// <summary>
        /// The model to build an axis from: that axis's own slot, falling back to the family's
        /// shared base so a half-filled row still produces all variants.
        /// </summary>
        public GameObject ModelSourceFor(BuildAxis axis)
        {
            GameObject perAxis = PerAxisModelSource(axis);
            if (perAxis != null)
            {
                return perAxis;
            }

            return FamilyModelSource(BuildAxisExtensions.Family(axis));
        }

        public void SetModelPrefab(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.X: modelPrefabX = prefab; break;
                case BuildAxis.Y: modelPrefabY = prefab; break;
                case BuildAxis.Z: modelPrefabZ = prefab; break;
                case BuildAxis.XY: modelPrefabXY = prefab; break;
                case BuildAxis.YZ: modelPrefabYZ = prefab; break;
                case BuildAxis.XZ: modelPrefabXZ = prefab; break;
                case BuildAxis.XYZ: modelPrefabXYZ = prefab; break;
                default: modelPrefabX = prefab; break;
            }
        }

        public GameObject ModelPrefabFor(BuildAxis axis) => axis switch
        {
            BuildAxis.X => modelPrefabX,
            BuildAxis.Y => modelPrefabY,
            BuildAxis.Z => modelPrefabZ,
            BuildAxis.XY => modelPrefabXY,
            BuildAxis.YZ => modelPrefabYZ,
            BuildAxis.XZ => modelPrefabXZ,
            BuildAxis.XYZ => modelPrefabXYZ,
            _ => modelPrefabX,
        };

        public void SetBreakPiece(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.X: breakPieceX = prefab; break;
                case BuildAxis.Y: breakPieceY = prefab; break;
                case BuildAxis.Z: breakPieceZ = prefab; break;
                case BuildAxis.XY: breakPieceXY = prefab; break;
                case BuildAxis.YZ: breakPieceYZ = prefab; break;
                case BuildAxis.XZ: breakPieceXZ = prefab; break;
                case BuildAxis.XYZ: breakPieceXYZ = prefab; break;
                default: breakPieceX = prefab; break;
            }
        }

        public GameObject BreakPieceFor(BuildAxis axis) => axis switch
        {
            BuildAxis.X => breakPieceX,
            BuildAxis.Y => breakPieceY,
            BuildAxis.Z => breakPieceZ,
            BuildAxis.XY => breakPieceXY,
            BuildAxis.YZ => breakPieceYZ,
            BuildAxis.XZ => breakPieceXZ,
            BuildAxis.XYZ => breakPieceXYZ,
            _ => breakPieceX,
        };
    }
}
