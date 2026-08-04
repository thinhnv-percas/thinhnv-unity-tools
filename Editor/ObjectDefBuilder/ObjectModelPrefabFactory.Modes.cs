using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The axis-variant modes that do not derive from a base: independent prefabs per axis, or one
    /// prefab shared across a family.
    /// </summary>
    public static partial class ObjectModelPrefabFactory
    {

        /// <summary>Independent prefabs, each from its own axis model slot.</summary>
        private static void BuildSeparate(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, ref ModelBuildResult result)
        {
            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                GameObject source = row.ModelSourceFor(axis);
                if (source == null)
                {
                    continue;
                }

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

        /// <summary>One prefab per magnitude per family, shared by all axis rows of that family.</summary>
        private static void BuildShared(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material material, int magnitude, ref ModelBuildResult result)
        {
            BuildSharedFamily(entry, row.modelSource, material, magnitude,
                BuildAxisExtensions.BarAxes, ref result);
            BuildSharedFamily(entry, row.plateModelSource, material, magnitude,
                BuildAxisExtensions.PlateAxes, ref result);
            BuildSharedFamily(entry, row.cubeModelSource, material, magnitude,
                BuildAxisExtensions.CubeAxes, ref result);
        }

        private static void BuildSharedFamily(ObjectDefBuildEntry entry, GameObject source,
            Material material, int magnitude, BuildAxis[] axes, ref ModelBuildResult result)
        {
            if (source == null)
            {
                return;
            }

            GameObject shared = Build(entry, source, material,
                entry.prefabFolder, ObjectDefNaming.ModelPrefab(entry, magnitude, axes[0]));
            BakeSize(entry, shared);

            foreach (BuildAxis axis in axes)
            {
                result.SetAxis(axis, shared);
            }
        }
    }
}
