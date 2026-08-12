using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>Object prefabs produced for one size, across all axis families.</summary>
    public struct ModelBuildResult
    {
        /// <summary>Per-family base the axis variants derive from (RotateBase mode only).</summary>
        public GameObject barBasePrefab;
        public GameObject plateBasePrefab;
        public GameObject cubeBasePrefab;

        /// <summary>The prefab for the uniform 1x1x1 size (no axis variants exist).</summary>
        public GameObject uniform;

        public GameObject axisX, axisY, axisZ;
        public GameObject axisXY, axisYZ, axisXZ;
        public GameObject axisXYZ;

        public GameObject BasePrefab(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => plateBasePrefab,
            BuildAxisFamily.Cube => cubeBasePrefab,
            _ => barBasePrefab,
        };

        public void SetBasePrefab(BuildAxisFamily family, GameObject prefab)
        {
            switch (family)
            {
                case BuildAxisFamily.Plate: plateBasePrefab = prefab; break;
                case BuildAxisFamily.Cube: cubeBasePrefab = prefab; break;
                default: barBasePrefab = prefab; break;
            }
        }

        public GameObject ForAxis(BuildAxis axis) => axis switch
        {
            BuildAxis.X => axisX, BuildAxis.Y => axisY, BuildAxis.Z => axisZ,
            BuildAxis.XY => axisXY, BuildAxis.YZ => axisYZ, BuildAxis.XZ => axisXZ,
            BuildAxis.XYZ => axisXYZ,
            _ => uniform,
        };

        public void SetAxis(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.X: axisX = prefab; break;
                case BuildAxis.Y: axisY = prefab; break;
                case BuildAxis.Z: axisZ = prefab; break;
                case BuildAxis.XY: axisXY = prefab; break;
                case BuildAxis.YZ: axisYZ = prefab; break;
                case BuildAxis.XZ: axisXZ = prefab; break;
                case BuildAxis.XYZ: axisXYZ = prefab; break;
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
                return result;
            }

            switch (entry.modelVariantMode)
            {
                case ModelVariantMode.RotateBase:
                    foreach (BuildAxisFamily family in FamiliesWithSource(row))
                    {
                        BuildRotatedFamily(entry, row, material, magnitude, family, ref result);
                    }
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

        private static readonly BuildAxisFamily[] AllFamilies =
            { BuildAxisFamily.Bar, BuildAxisFamily.Plate, BuildAxisFamily.Cube };

        private static System.Collections.Generic.List<BuildAxisFamily> FamiliesWithSource(ObjectDefBuildRow row)
        {
            var list = new System.Collections.Generic.List<BuildAxisFamily>(3);
            foreach (BuildAxisFamily family in AllFamilies)
            {
                if (row.FamilyModelSource(family) != null)
                {
                    list.Add(family);
                }
            }
            return list;
        }

        /// <summary>
        /// Build one family's base prefab and its axis variants. Axes that carry their own model slot
        /// are built from that model directly instead of rotating the base.
        /// </summary>
        private static void BuildRotatedFamily(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, BuildAxisFamily family, ref ModelBuildResult result)
        {
            BuildAxis[] axes = BuildAxisExtensions.FamilyAxes(family);
            GameObject source = row.FamilyModelSource(family);
            if (source == null)
            {
                return;
            }

            bool anyRotated = false;
            foreach (BuildAxis axis in axes)
            {
                anyRotated |= row.PerAxisModelSource(axis) == null;
            }

            if (anyRotated)
            {
                GameObject basePrefab = Build(entry, source, material,
                    ObjectDefNaming.BaseFolder(entry),
                    ObjectDefNaming.ModelBasePrefab(entry, magnitude, family));
                result.SetBasePrefab(family, basePrefab);

                if (entry.keepDecalRotation)
                {
                    AxisVariantFactory.CacheDecalNames(basePrefab, entry.decalCompensationAxes);
                }
            }

            foreach (BuildAxis axis in axes)
            {
                if (row.PerAxisModelSource(axis) != null)
                {
                    GameObject own = BuildAxisModel(entry, row, material, magnitude, axis);
                    result.SetAxis(axis, own);
                    continue;
                }

                GameObject basePrefab = result.BasePrefab(family);
                GameObject variant = Variant(entry, basePrefab, magnitude, axis);
                result.SetAxis(axis, variant);
            }
        }

        /// <summary>
        /// The axis variant, falling back to <see cref="ObjectDefBuildEntry.BarRotationOverride"/> when the
        /// Bar family's source isn't authored along Y.
        /// </summary>
        private static GameObject Variant(ObjectDefBuildEntry entry, GameObject basePrefab, int magnitude,
            BuildAxis axis)
        {
            Quaternion? authored = entry.BarRotationOverride(axis);

            return AxisVariantFactory.CreateOrRefresh(basePrefab, entry.prefabFolder,
                ObjectDefNaming.ModelPrefab(entry, magnitude, axis), axis, entry.overwritePrefabs, authored,
                entry.keepDecalRotation ? entry.decalCompensationAxes : null);
        }
    }
}
