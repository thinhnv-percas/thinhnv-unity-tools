using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>Prefabs produced for one size's shatter effect, across all axis families.</summary>
    public struct BreakBuildResult
    {
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
            }
        }
    }

    /// <summary>
    /// Builds the shatter (<c>BreakPieceEffect</c>) prefabs for one object size out of a fractured model.
    ///
    /// Each family (Bar/Plate/Cube) produces its own base + axis variants from the family's break
    /// source. Families whose break source is null are skipped.
    /// </summary>
    public static partial class BreakPiecePrefabFactory
    {
        private static readonly BuildAxisFamily[] AllFamilies =
            { BuildAxisFamily.Bar, BuildAxisFamily.Plate, BuildAxisFamily.Cube };

        /// <summary>
        /// Generate the shatter prefabs for <paramref name="magnitude"/> across all families that
        /// have a break source on the row. Magnitude 1 yields a single uniform prefab.
        /// </summary>
        public static BreakBuildResult Build(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            Material pieceMaterial, int magnitude)
        {
            var result = new BreakBuildResult();
            if (SmashMarketBridge.BreakPieceEffectType == null)
            {
                return result;
            }

            if (magnitude <= 1)
            {
                GameObject breakSource = row.breakSource;
                if (breakSource != null)
                {
                    result.uniform = BuildBase(entry, breakSource, pieceMaterial, entry.prefabFolder,
                        ObjectDefNaming.BreakPiecePrefab(entry, 1, BuildAxis.None));
                }
                return result;
            }

            foreach (BuildAxisFamily family in AllFamilies)
            {
                GameObject breakSource = row.FamilyBreakSource(family);
                if (breakSource == null)
                {
                    continue;
                }

                BuildFamily(entry, row, breakSource, pieceMaterial, magnitude, family, ref result);
            }

            return result;
        }

        private static void BuildFamily(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            GameObject breakSource, Material pieceMaterial, int magnitude,
            BuildAxisFamily family, ref BreakBuildResult result)
        {
            BuildAxis[] axes = BuildAxisExtensions.FamilyAxes(family);
            string baseFolder = ObjectDefNaming.BaseFolder(entry);

            GameObject basePrefab = BuildBase(entry, breakSource, pieceMaterial, baseFolder,
                ObjectDefNaming.BreakBasePrefab(entry, magnitude, family));
            if (basePrefab == null)
            {
                return;
            }

            switch (family)
            {
                case BuildAxisFamily.Bar: result.barBasePrefab = basePrefab; break;
                case BuildAxisFamily.Plate: result.plateBasePrefab = basePrefab; break;
                case BuildAxisFamily.Cube: result.cubeBasePrefab = basePrefab; break;
            }

            foreach (BuildAxis axis in axes)
            {
                result.SetAxis(axis, BuildVariant(entry, basePrefab, magnitude, axis));
            }
        }

        private static GameObject BuildVariant(ObjectDefBuildEntry entry, GameObject basePrefab,
            int magnitude, BuildAxis axis)
        {
            return AxisVariantFactory.CreateOrRefresh(basePrefab, entry.prefabFolder,
                ObjectDefNaming.BreakPiecePrefab(entry, magnitude, axis), axis, entry.overwritePrefabs);
        }

        /// <summary>
        /// Write the pieces prefab that every axis variant derives from. Returns the existing asset
        /// untouched when <see cref="ObjectDefBuildEntry.overwritePrefabs"/> is off.
        /// </summary>
        private static GameObject BuildBase(ObjectDefBuildEntry entry, GameObject breakSource,
            Material pieceMaterial, string folder, string prefabName)
        {
            string path = $"{folder}/{prefabName}.prefab";
            if (!entry.overwritePrefabs)
            {
                var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (existing != null)
                {
                    return existing;
                }
            }

            var root = new GameObject(prefabName);
            try
            {
                Transform content = CreateContentRoot(entry, root);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(breakSource, root.transform);
                instance.transform.SetLocalPositionAndRotation(
                    Vector3.zero, Quaternion.Euler(entry.breakRotation));
                instance.transform.localScale = Vector3.one;

                int pieces = ExtractPieces(entry, content, instance, pieceMaterial);
                Object.DestroyImmediate(instance);

                if (pieces == 0)
                {
                    Debug.LogWarning($"[ObjectDefBuilder] '{breakSource.name}' has no mesh pieces; " +
                                     $"'{prefabName}' was not created.");
                    return null;
                }

                ToolAssetUtil.SetLayerRecursively(root.transform, entry.pieceLayer);
                WirePieceRigidbodies(root);

                ToolAssetUtil.EnsureFolder(folder);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
