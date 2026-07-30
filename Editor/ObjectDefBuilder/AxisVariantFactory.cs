using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Makes the three per-axis prefab variants of a base prefab by rotating the base's direct children
    /// onto that axis. Used for both the shatter prefabs (children = piece pivots) and the object prefabs
    /// (child = the wrapper holding collider + model).
    ///
    /// Rotating children rather than the root is what keeps the root's own components untouched across
    /// variants - the Rigidbody / ObjectElement / BreakPieceEffect stay exactly as the base authored them,
    /// which is why <c>BreakPieceEffect</c> can cache one set of local piece transforms and have it hold
    /// for all three axes.
    ///
    /// The models are authored along Y, so Y is identity, X is -90 about Z and Z is +90 about X - see
    /// <see cref="BuildAxisExtensions.Rotation"/>.
    /// </summary>
    public static class AxisVariantFactory
    {
        /// <summary>
        /// Create "<c>{folder}/{prefabName}.prefab</c>" as a variant of <paramref name="basePrefab"/>, or
        /// refresh it in place when it already exists. Child poses are written as absolute values derived
        /// from the base, so re-running is idempotent instead of rotating an already-rotated variant.
        /// Returns the existing asset untouched when <paramref name="overwrite"/> is false.
        /// </summary>
        public static GameObject CreateOrRefresh(GameObject basePrefab, string folder, string prefabName,
            BuildAxis axis, bool overwrite)
        {
            if (basePrefab == null)
            {
                return null;
            }

            ToolAssetUtil.EnsureFolder(folder);
            string path = $"{folder}/{prefabName}.prefab";

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing == null)
            {
                return Create(basePrefab, path, prefabName, axis);
            }

            return overwrite ? Refresh(basePrefab, path, prefabName, axis) : existing;
        }

        private static GameObject Create(GameObject basePrefab, string path, string prefabName, BuildAxis axis)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                instance.name = prefabName;
                ApplyAxis(basePrefab.transform, instance.transform, axis);
                return PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        /// <summary>
        /// Rewrite the child overrides of an existing variant in place, keeping its asset (and therefore
        /// every reference to it) rather than replacing it with a freshly instantiated one.
        /// </summary>
        private static GameObject Refresh(GameObject basePrefab, string path, string prefabName, BuildAxis axis)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (PrefabUtility.GetCorrespondingObjectFromSource(contents) != basePrefab)
                {
                    Debug.LogWarning($"[ObjectDefBuilder] '{path}' is not a variant of " +
                                     $"'{basePrefab.name}'; it was left untouched. Delete it if you want " +
                                     "the builder to write a variant there.");
                    return AssetDatabase.LoadAssetAtPath<GameObject>(path);
                }

                contents.name = prefabName;
                ApplyAxis(basePrefab.transform, contents.transform, axis);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        /// <summary>
        /// Set every direct child of <paramref name="variantRoot"/> to its counterpart in
        /// <paramref name="baseRoot"/> rotated into <paramref name="axis"/>. Children are matched by index,
        /// which holds because the variant is derived from the base.
        /// </summary>
        private static void ApplyAxis(Transform baseRoot, Transform variantRoot, BuildAxis axis)
        {
            Quaternion rotation = axis.Rotation();
            int count = Mathf.Min(baseRoot.childCount, variantRoot.childCount);

            if (baseRoot.childCount != variantRoot.childCount)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{variantRoot.name}' has {variantRoot.childCount} " +
                                 $"child(ren) but its base has {baseRoot.childCount}; only the first " +
                                 $"{count} were aligned.");
            }

            for (int i = 0; i < count; i++)
            {
                Transform source = baseRoot.GetChild(i);
                Transform child = variantRoot.GetChild(i);
                child.localPosition = rotation * source.localPosition;
                child.localRotation = rotation * source.localRotation;
            }
        }
    }
}
