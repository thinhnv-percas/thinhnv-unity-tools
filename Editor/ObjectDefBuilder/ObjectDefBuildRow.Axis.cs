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

        [Header("Rotation already baked into each axis mesh")]
        public Quaternion bakedRotationX;
        public Quaternion bakedRotationY;
        public Quaternion bakedRotationZ;
        public Quaternion bakedRotationXY;
        public Quaternion bakedRotationYZ;
        public Quaternion bakedRotationXZ;
        public Quaternion bakedRotationXYZ;

        public Quaternion BakedRotationFor(BuildAxis axis)
        {
            Quaternion stored = RawBakedRotation(axis);
            return HasBakedRotation(axis) ? stored : Quaternion.identity;
        }

        public bool HasBakedRotation(BuildAxis axis)
        {
            Quaternion stored = RawBakedRotation(axis);
            return stored.x != 0f || stored.y != 0f || stored.z != 0f || stored.w != 0f;
        }

        public void SetBakedRotation(BuildAxis axis, Quaternion rotation)
        {
            switch (axis)
            {
                case BuildAxis.X: bakedRotationX = rotation; break;
                case BuildAxis.Y: bakedRotationY = rotation; break;
                case BuildAxis.Z: bakedRotationZ = rotation; break;
                case BuildAxis.XY: bakedRotationXY = rotation; break;
                case BuildAxis.YZ: bakedRotationYZ = rotation; break;
                case BuildAxis.XZ: bakedRotationXZ = rotation; break;
                case BuildAxis.XYZ: bakedRotationXYZ = rotation; break;
                default: bakedRotationX = rotation; break;
            }
        }

        private Quaternion RawBakedRotation(BuildAxis axis) => axis switch
        {
            BuildAxis.X => bakedRotationX,
            BuildAxis.Y => bakedRotationY,
            BuildAxis.Z => bakedRotationZ,
            BuildAxis.XY => bakedRotationXY,
            BuildAxis.YZ => bakedRotationYZ,
            BuildAxis.XZ => bakedRotationXZ,
            BuildAxis.XYZ => bakedRotationXYZ,
            _ => bakedRotationX,
        };
    }
}
