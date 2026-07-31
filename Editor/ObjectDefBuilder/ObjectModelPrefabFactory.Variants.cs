using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>Object prefabs produced for one size.</summary>
    public struct ModelBuildResult
    {
        /// <summary>Shared base the axis variants derive from; only set in <see cref="ModelVariantMode.RotateBase"/>.</summary>
        public GameObject basePrefab;

        /// <summary>The prefab for the uniform 1x1x1 size (no axis variants exist).</summary>
        public GameObject uniform;

        public GameObject axisX;
        public GameObject axisY;
        public GameObject axisZ;

        public GameObject ForAxis(BuildAxis axis) => axis switch
        {
            BuildAxis.X => axisX,
            BuildAxis.Y => axisY,
            BuildAxis.Z => axisZ,
            _ => uniform,
        };

        public void SetAxis(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.X: axisX = prefab; break;
                case BuildAxis.Y: axisY = prefab; break;
                case BuildAxis.Z: axisZ = prefab; break;
                default: uniform = prefab; break;
            }
        }
    }

    /// <summary>
    /// The three axis variants of an object prefab, per <see cref="ModelVariantMode"/>.
    ///
    /// This exists because the level data carries no orientation - every object's "rotation" is zero in
    /// all levels and <c>SpawnControllerView.SpawnObject</c> applies it verbatim - so a stretched object
    /// only ends up along the right axis if its prefab is already oriented that way.
    /// </summary>
    public static partial class ObjectModelPrefabFactory
    {
        /// <summary>Build every object prefab one size needs, following the entry's variant mode.</summary>
        public static ModelBuildResult BuildForSize(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude)
        {
            var result = new ModelBuildResult();

            if (magnitude <= 1)
            {
                result.uniform = Build(entry, row.modelSource, material,
                    entry.prefabFolder, ObjectDefNaming.ModelPrefab(entry, 1, BuildAxis.None));
                BakeSize(entry, result.uniform);
                return result;
            }

            switch (entry.modelVariantMode)
            {
                case ModelVariantMode.RotateBase:
                    BuildRotatedVariants(entry, row, material, magnitude, ref result);
                    break;

                case ModelVariantMode.Shared:
                    BuildShared(entry, row, material, magnitude, ref result);
                    break;

                default:
                    BuildSeparate(entry, row, material, magnitude, ref result);
                    break;
            }

            return result;
        }

        /// <summary>
        /// One base prefab, then a variant per axis with the model rotated onto it - except for any axis
        /// that carries its own model, which is built straight from that model instead. That lets a size
        /// mix the two: rotate the shared model where the rotation is right, and drop a purpose-built
        /// model on the axes where it is not.
        ///
        /// The base is only written when at least one axis still needs it, so overriding all three does
        /// not leave a stray *_ModelBase behind.
        /// </summary>
        private static void BuildRotatedVariants(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, ref ModelBuildResult result)
        {
            bool anyRotated = false;
            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                anyRotated |= row.PerAxisModelSource(axis) == null;
            }

            if (anyRotated)
            {
                result.basePrefab = Build(entry, row.modelSource, material,
                    ObjectDefNaming.BaseFolder(entry), ObjectDefNaming.ModelBasePrefab(entry, magnitude));

                // Bake before the variants exist, so they derive from the already-flattened base.
                if (entry.bakeBaseMesh)
                {
                    MeshBakeFactory.Bake(entry, result.basePrefab);
                }
            }

            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                if (row.PerAxisModelSource(axis) != null)
                {
                    GameObject own = BuildAxisModel(entry, row, material, magnitude, axis);
                    BakeSize(entry, own);
                    result.SetAxis(axis, own);
                    continue;
                }

                // A variant inherits the base hierarchy, so it is baked via the base, never on its own.
                result.SetAxis(axis, Variant(entry, result.basePrefab, magnitude, axis));
            }
        }

        /// <summary>Bake a standalone size prefab when the option is on; variants are handled by the base.</summary>
        private static void BakeSize(ObjectDefBuildEntry entry, GameObject prefab)
        {
            if (entry.bakeSizeMesh && prefab != null)
            {
                MeshBakeFactory.Bake(entry, prefab);
            }
        }

        private static GameObject Variant(ObjectDefBuildEntry entry, GameObject basePrefab,
            int magnitude, BuildAxis axis)
        {
            return AxisVariantFactory.CreateOrRefresh(basePrefab, entry.prefabFolder,
                ObjectDefNaming.ModelPrefab(entry, magnitude, axis), axis, entry.overwritePrefabs);
        }

        /// <summary>Three independent prefabs, each from its own axis model slot.</summary>
        private static void BuildSeparate(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, ref ModelBuildResult result)
        {
            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                GameObject prefab = BuildAxisModel(entry, row, material, magnitude, axis);
                BakeSize(entry, prefab);
                result.SetAxis(axis, prefab);
            }
        }

        private static GameObject BuildAxisModel(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, BuildAxis axis)
        {
            return Build(entry, row.ModelSourceFor(axis), material,
                entry.prefabFolder, ObjectDefNaming.ModelPrefab(entry, magnitude, axis));
        }

        /// <summary>One prefab for the whole magnitude, wired into all three axis rows.</summary>
        private static void BuildShared(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, ref ModelBuildResult result)
        {
            GameObject shared = Build(entry, row.modelSource, material,
                entry.prefabFolder, ObjectDefNaming.ModelPrefab(entry, magnitude, BuildAxis.X));
            BakeSize(entry, shared);

            result.axisX = shared;
            result.axisY = shared;
            result.axisZ = shared;
        }
    }
}
