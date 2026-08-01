using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The axis-variant modes that do not derive from a base: three independent prefabs per axis, or one
    /// prefab shared across all three.
    /// </summary>
    public static partial class ObjectModelPrefabFactory
    {

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
