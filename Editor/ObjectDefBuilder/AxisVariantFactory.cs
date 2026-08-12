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
        /// <param name="decalAxes">
        /// Null to leave decals alone. Otherwise, every named decal in <paramref name="basePrefab"/> (see
        /// <see cref="CacheDecalNames"/>) has its local rotation set to the value hand-authored for
        /// <paramref name="axis"/> in its face's <see cref="DecalFaceRotations"/>. See
        /// <see cref="FixDecalRotations"/>.
        /// </param>
        public static GameObject CreateOrRefresh(GameObject basePrefab, string folder, string prefabName,
            BuildAxis axis, bool overwrite, Quaternion? rotation = null, DecalCompensationAxes decalAxes = null)
        {
            if (basePrefab == null)
            {
                return null;
            }

            ToolAssetUtil.EnsureFolder(folder);
            string path = $"{folder}/{prefabName}.prefab";
            Quaternion applied = rotation ?? axis.Rotation();

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing == null)
            {
                return Create(basePrefab, path, prefabName, applied, axis, decalAxes);
            }

            AssetDatabase.DeleteAsset(path);
            return Create(basePrefab, path, prefabName, applied, axis, decalAxes);

            //return overwrite ? Refresh(basePrefab, path, prefabName, applied, axis, decalAxes) : existing;
        }

        private static GameObject Create(GameObject basePrefab, string path, string prefabName,
            Quaternion rotation, BuildAxis axis, DecalCompensationAxes decalAxes)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                instance.name = prefabName;
                ApplyAxis(basePrefab.transform, instance.transform, rotation);
                if (decalAxes != null)
                {
                    FixDecalRotations(instance.transform, axis, decalAxes);
                }
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
        private static GameObject Refresh(GameObject basePrefab, string path, string prefabName,
            Quaternion rotation, BuildAxis axis, DecalCompensationAxes decalAxes)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var variantSource = PrefabUtility.GetCorrespondingObjectFromSource(contents);
                var variantSourcePath = variantSource == null ? string.Empty : AssetDatabase.GetAssetPath(variantSource);
                var basePrefabPath = basePrefab == null ? string.Empty : AssetDatabase.GetAssetPath(basePrefab);
                if (string.IsNullOrEmpty(variantSourcePath) || variantSourcePath != basePrefabPath)

                {
                    Debug.LogWarning($"[ObjectDefBuilder] '{path}' is not a variant of " +
                                     $"'{basePrefab.name}'; it was left untouched. Delete it if you want " +
                                     "the builder to write a variant there.");
                    return AssetDatabase.LoadAssetAtPath<GameObject>(path);
                }

                contents.name = prefabName;
                ApplyAxis(basePrefab.transform, contents.transform, rotation);
                if (decalAxes != null)
                {
                    FixDecalRotations(contents.transform, axis, decalAxes);
                }
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
        /// <paramref name="baseRoot"/> turned by <paramref name="rotation"/>. Children are matched by index,
        /// which holds because the variant is derived from the base.
        /// </summary>
        private static void ApplyAxis(Transform baseRoot, Transform variantRoot, Quaternion rotation)
        {
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

        private static readonly DecalDirection[] AllDirections =
        {
            DecalDirection.PosX, DecalDirection.NegX,
            DecalDirection.PosY, DecalDirection.NegY,
            DecalDirection.PosZ, DecalDirection.NegZ,
        };

        /// <summary>
        /// Scans <paramref name="baseModel"/> for decal renderers and writes each one's actual name into
        /// <paramref name="decalAxes"/>'s slot for the face its local position places it on (see
        /// <see cref="DetectDirection"/>). Run once, right after the base model prefab is built from its
        /// source FBX, so every axis variant derived from that base can later find each decal by name (see
        /// <see cref="FixDecalRotations"/>) instead of re-detecting its face or depending on the variant
        /// mirroring the base node-for-node.
        /// </summary>
        public static void CacheDecalNames(GameObject baseModel, DecalCompensationAxes decalAxes)
        {
            if (baseModel == null || decalAxes == null)
            {
                return;
            }

            CollectDecalNames(baseModel.transform, decalAxes);
        }

        private static void CollectDecalNames(Transform node, DecalCompensationAxes decalAxes)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                Transform child = node.GetChild(i);
                if (IsDecalRenderer(child))
                {
                    decalAxes.SetName(DetectDirection(child.localPosition), child.name);
                }

                CollectDecalNames(child, decalAxes);
            }
        }

        /// <summary>
        /// For each of the six directions in <paramref name="decalAxes"/> that names a decal, finds that
        /// decal by name in <paramref name="variantRoot"/> - names are cached once from the base by
        /// <see cref="CacheDecalNames"/> - and sets its local rotation to the value hand-authored for
        /// <paramref name="axis"/> in that face's <see cref="DecalFaceRotations"/>. Looking decals up by
        /// name, rather than walking the base and variant hierarchies in lock-step by child index, means
        /// the variant doesn't need to mirror the base node-for-node. Only applies to the Bar family's
        /// three axes (X/Y/Z); any other <paramref name="axis"/> is left untouched. Position is left alone;
        /// only rotation is touched.
        /// </summary>
        private static void FixDecalRotations(Transform variantRoot, BuildAxis axis,
            DecalCompensationAxes decalAxes)
        {
            if (axis != BuildAxis.X && axis != BuildAxis.Y && axis != BuildAxis.Z)
            {
                return;
            }

            foreach (DecalDirection direction in AllDirections)
            {
                string name = decalAxes.Name(direction);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                Transform variantNode = FindRecursive(variantRoot, name);
                if (variantNode == null)
                {
                    continue;
                }

                variantNode.localRotation = Quaternion.Euler(decalAxes.Rotations(direction).For(axis));
            }
        }

        /// <summary>The first of <paramref name="root"/> or its descendants named <paramref name="name"/>.</summary>
        private static Transform FindRecursive(Transform root, string name)
        {
            if (root.name == name)
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindRecursive(root.GetChild(i), name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>A MeshRenderer whose parent also carries a MeshRenderer - a decal submesh stuck onto it.</summary>
        private static bool IsDecalRenderer(Transform node)
        {
            return node.TryGetComponent<MeshRenderer>(out _) &&
                   node.parent != null && node.parent.TryGetComponent<MeshRenderer>(out _);
        }

        /// <summary>The face a decal's local position (relative to the mesh it is stuck on) places it on.</summary>
        private static DecalDirection DetectDirection(Vector3 localPosition)
        {
            float ax = Mathf.Abs(localPosition.x);
            float ay = Mathf.Abs(localPosition.y);
            float az = Mathf.Abs(localPosition.z);

            if (ax >= ay && ax >= az)
            {
                return localPosition.x >= 0f ? DecalDirection.PosX : DecalDirection.NegX;
            }

            if (ay >= az)
            {
                return localPosition.y >= 0f ? DecalDirection.PosY : DecalDirection.NegY;
            }

            return localPosition.z >= 0f ? DecalDirection.PosZ : DecalDirection.NegZ;
        }
    }
}
