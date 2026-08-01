using System;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// One authored size of an object: the sources the user dragged in plus the assets the last build
    /// produced from them. <see cref="magnitude"/> 1 is the uniform 1x1x1 base, which has no axis variants.
    /// </summary>
    [Serializable]
    public class ObjectDefBuildRow
    {
        public int magnitude = 1;
        public bool include = true;

        [Header("Sources (drag here)")]
        public GameObject modelSource;
        public GameObject breakSource;
        public Texture2D modelTexture;
        public Texture2D pieceTexture;

        [Header("Per-axis models (ModelVariantMode.SeparateModels only)")]
        public GameObject modelSourceX;
        public GameObject modelSourceY;
        public GameObject modelSourceZ;

        [Header("Last build result (cache)")]
        public Material modelMaterial;
        public Material pieceMaterial;
        public GameObject modelBasePrefab;
        public GameObject modelPrefabX;
        public GameObject modelPrefabY;
        public GameObject modelPrefabZ;
        public GameObject breakBasePrefab;
        public GameObject breakPieceX;
        public GameObject breakPieceY;
        public GameObject breakPieceZ;

        public bool HasAnySource =>
            modelSource != null || breakSource != null ||
            modelSourceX != null || modelSourceY != null || modelSourceZ != null;

        /// <summary>
        /// True when a previous build left object prefabs here, which is what the Bake buttons act on -
        /// they work off the cache, not the source models, so prefabs built before baking existed can
        /// still be flattened.
        /// </summary>
        public bool HasBakeTarget =>
            modelBasePrefab != null || modelPrefabX != null || modelPrefabY != null || modelPrefabZ != null;

        /// <summary>
        /// The model dropped on one axis specifically, or null when that axis has none. A non-null value
        /// means "build this axis from its own model" rather than rotating the shared base.
        /// </summary>
        public GameObject PerAxisModelSource(BuildAxis axis) => axis switch
        {
            BuildAxis.X => modelSourceX,
            BuildAxis.Y => modelSourceY,
            BuildAxis.Z => modelSourceZ,
            _ => null,
        };

        public void SetPerAxisModelSource(BuildAxis axis, GameObject source)
        {
            switch (axis)
            {
                case BuildAxis.X: modelSourceX = source; break;
                case BuildAxis.Y: modelSourceY = source; break;
                case BuildAxis.Z: modelSourceZ = source; break;
            }
        }

        /// <summary>True when at least one axis carries its own model.</summary>
        public bool HasPerAxisModels =>
            modelSourceX != null || modelSourceY != null || modelSourceZ != null;

        /// <summary>
        /// The model to build an axis from: that axis's own slot, falling back to the shared
        /// <see cref="modelSource"/> so a half-filled row still produces all three variants.
        /// </summary>
        public GameObject ModelSourceFor(BuildAxis axis)
        {
            GameObject perAxis = PerAxisModelSource(axis);
            return perAxis != null ? perAxis : modelSource;
        }

        /// <summary>Store the prefab built for one axis (X is also the slot used by magnitude 1).</summary>
        public void SetModelPrefab(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.Y: modelPrefabY = prefab; break;
                case BuildAxis.Z: modelPrefabZ = prefab; break;
                default: modelPrefabX = prefab; break;
            }
        }

        public GameObject ModelPrefabFor(BuildAxis axis) => axis switch
        {
            BuildAxis.Y => modelPrefabY,
            BuildAxis.Z => modelPrefabZ,
            _ => modelPrefabX,
        };

        public void SetBreakPiece(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.Y: breakPieceY = prefab; break;
                case BuildAxis.Z: breakPieceZ = prefab; break;
                default: breakPieceX = prefab; break;
            }
        }

        public GameObject BreakPieceFor(BuildAxis axis) => axis switch
        {
            BuildAxis.Y => breakPieceY,
            BuildAxis.Z => breakPieceZ,
            _ => breakPieceX,
        };

        [Header("Rotation already baked into each axis mesh")]
        public Quaternion bakedRotationX;
        public Quaternion bakedRotationY;
        public Quaternion bakedRotationZ;

        /// <summary>
        /// The rotation currently baked into an axis mesh's vertices. Needed because Mesh Per Axis moves the
        /// rotation off the transform, so a second bake would otherwise read identity and lose it.
        ///
        /// An unset field deserializes as the zero quaternion, which is not a rotation - that reads as
        /// "nothing baked yet".
        /// </summary>
        public Quaternion BakedRotationFor(BuildAxis axis)
        {
            Quaternion stored = axis switch
            {
                BuildAxis.Y => bakedRotationY,
                BuildAxis.Z => bakedRotationZ,
                _ => bakedRotationX,
            };

            return HasBakedRotation(axis) ? stored : Quaternion.identity;
        }

        /// <summary>
        /// Whether an axis rotation has been captured yet. Once it has, it is the authored angle - a rebuild
        /// re-applies it instead of the canonical one, so a hand-tweaked angle is not silently reset.
        /// </summary>
        public bool HasBakedRotation(BuildAxis axis)
        {
            Quaternion stored = axis switch
            {
                BuildAxis.Y => bakedRotationY,
                BuildAxis.Z => bakedRotationZ,
                _ => bakedRotationX,
            };

            // An unset Quaternion field deserializes as (0,0,0,0), which is not a rotation.
            return stored.x != 0f || stored.y != 0f || stored.z != 0f || stored.w != 0f;
        }

        public void SetBakedRotation(BuildAxis axis, Quaternion rotation)
        {
            switch (axis)
            {
                case BuildAxis.Y: bakedRotationY = rotation; break;
                case BuildAxis.Z: bakedRotationZ = rotation; break;
                default: bakedRotationX = rotation; break;
            }
        }
    }
}
