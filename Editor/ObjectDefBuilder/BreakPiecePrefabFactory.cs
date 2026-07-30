using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>Prefabs produced for one size's shatter effect.</summary>
    public struct BreakBuildResult
    {
        /// <summary>Shared base holding the pieces; null for the uniform 1x1x1 size.</summary>
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
    }

    /// <summary>
    /// Builds the shatter (<c>BreakPieceEffect</c>) prefabs for one object size out of a fractured model.
    ///
    /// Structure written, matching the shipped *_BreakBase prefabs: root (BreakPieceEffect) -> one pivot
    /// per piece carrying the piece's pose -> the piece itself at local identity with mesh, convex
    /// MeshCollider and Rigidbody. The pose lives on the pivot so the Rigidbody's local pose stays
    /// identity in every axis variant, which is what lets <c>BreakPieceEffect</c> restore its pieces
    /// from a single set of cached local transforms.
    ///
    /// Sizes above 1x1 additionally get three prefab variants of that base, one per stretched axis.
    /// </summary>
    public static partial class BreakPiecePrefabFactory
    {
        /// <summary>
        /// Generate the shatter prefabs for <paramref name="magnitude"/>. Magnitude 1 yields a single
        /// "<c>{prefix}_1x1_Break_Piece</c>"; larger sizes yield a "<c>Base/{prefix}_{n}_BreakBase</c>"
        /// plus "<c>{prefix}_{n}x|y|z_Break_Piece</c>" variants of it.
        /// </summary>
        public static BreakBuildResult Build(ObjectDefBuildEntry entry, GameObject breakSource,
            Material pieceMaterial, int magnitude)
        {
            var result = new BreakBuildResult();
            if (breakSource == null || SmashMarketBridge.BreakPieceEffectType == null)
            {
                return result;
            }

            string folder = entry.prefabFolder;

            if (magnitude <= 1)
            {
                result.uniform = BuildBase(entry, breakSource, pieceMaterial, folder,
                    ObjectDefNaming.BreakPiecePrefab(entry, 1, BuildAxis.None));
                return result;
            }

            result.basePrefab = BuildBase(entry, breakSource, pieceMaterial,
                ObjectDefNaming.BaseFolder(entry), ObjectDefNaming.BreakBasePrefab(entry, magnitude));
            if (result.basePrefab == null)
            {
                return result;
            }

            result.axisX = BuildVariant(entry, result.basePrefab, magnitude, BuildAxis.X);
            result.axisY = BuildVariant(entry, result.basePrefab, magnitude, BuildAxis.Y);
            result.axisZ = BuildVariant(entry, result.basePrefab, magnitude, BuildAxis.Z);
            return result;
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
