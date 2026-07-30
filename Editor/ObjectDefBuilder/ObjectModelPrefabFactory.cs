using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Builds a smashable object prefab from a dragged model, mirroring the shipped prefabs under
    /// Assets/_Use/GameObject/:
    /// <code>
    /// root      tag + layer, Rigidbody, ObjectElement
    /// +- Wrapper    the collider, and the Model Rotation
    ///    +- model   the dragged FBX (unpacked by default)
    /// </code>
    /// With <see cref="ObjectDefBuildEntry.useWrapper"/> off the model is parented straight to the root
    /// and a convex collider goes on the renderer that owns the mesh instead.
    /// </summary>
    public static partial class ObjectModelPrefabFactory
    {
        /// <summary>
        /// Write <paramref name="prefabName"/>.prefab into <paramref name="folder"/> from
        /// <paramref name="modelSource"/> (an FBX or prefab). An existing asset at that path is overwritten,
        /// or returned untouched when <see cref="ObjectDefBuildEntry.overwritePrefabs"/> is off.
        /// </summary>
        public static GameObject Build(ObjectDefBuildEntry entry, GameObject modelSource,
            Material material, string folder, string prefabName)
        {
            if (modelSource == null || SmashMarketBridge.ObjectElementType == null)
            {
                return null;
            }

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
                GameObject colliderOwner = AttachModel(entry, root, modelSource);
                ObjectDefMaterialFactory.ApplyToRenderers(root, material);
                ToolAssetUtil.SetLayerRecursively(root.transform, entry.objectLayer);
                ToolAssetUtil.TrySetTag(root, entry.objectTag);

                Rigidbody body = root.AddComponent<Rigidbody>();
                body.mass = entry.objectMass;

                Collider collider = AddCollider(entry, colliderOwner);
                WireObjectElement(root, body, collider);

                ToolAssetUtil.EnsureFolder(folder);
                return PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// Parent the model under the root, inside a wrapper child when one is asked for. Returns the
        /// GameObject the collider belongs on: the wrapper, or the root when there is none.
        ///
        /// The wrapper - not the model instance - takes the Model Rotation, so the model sits at local
        /// identity inside it and a convex mesh placed on the wrapper stays aligned with the visuals.
        /// </summary>
        private static GameObject AttachModel(ObjectDefBuildEntry entry, GameObject root, GameObject modelSource)
        {
            GameObject owner = root;
            Quaternion rotation = Quaternion.Euler(entry.modelRotation);

            if (entry.useWrapper)
            {
                owner = new GameObject(entry.EffectiveWrapperName);
                owner.transform.SetParent(root.transform, false);
                owner.transform.localRotation = rotation;
                rotation = Quaternion.identity;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(modelSource, owner.transform);
            instance.transform.SetLocalPositionAndRotation(Vector3.zero, rotation);
            instance.transform.localScale = Vector3.one;

            if (entry.unpackModel)
            {
                PrefabUtility.UnpackPrefabInstance(
                    instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }

            return owner;
        }

        /// <summary>
        /// Add the configured collider to <paramref name="owner"/> (the wrapper, or the root). A box is
        /// fitted to the model's bounds in that space. A convex mesh needs the mesh's own local space, so
        /// without a wrapper it goes on the renderer that owns the mesh - the same place the shipped
        /// prefabs put it.
        /// </summary>
        private static Collider AddCollider(ObjectDefBuildEntry entry, GameObject owner)
        {
            if (entry.objectColliderMode == ColliderMode.None)
            {
                return null;
            }

            if (entry.objectColliderMode == ColliderMode.BoxBounds)
            {
                ToolAssetUtil.TryLocalBounds(owner, out Bounds bounds);
                return ColliderFactory.Add(owner, ColliderMode.BoxBounds, null, bounds,
                    entry.roundObjectColliderSize, entry.objectPhysicMaterial);
            }

            MeshRenderer renderer = ToolAssetUtil.LargestRenderer(owner);
            if (renderer == null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{owner.name}' has no renderers to collide.");
                return null;
            }

            renderer.TryGetComponent(out MeshFilter filter);
            GameObject meshOwner = entry.useWrapper ? owner : renderer.gameObject;
            if (entry.useWrapper)
            {
                WarnIfMeshOffset(owner, renderer.transform);
            }

            return ColliderFactory.Add(meshOwner, ColliderMode.MeshConvex,
                filter == null ? null : filter.sharedMesh, default, false, entry.objectPhysicMaterial);
        }

        /// <summary>
        /// A convex mesh on the wrapper is only correct while the mesh's node sits at identity relative to
        /// it. Flag the mismatch instead of silently producing a shifted collider.
        /// </summary>
        private static void WarnIfMeshOffset(GameObject wrapper, Transform meshNode)
        {
            Matrix4x4 relative = wrapper.transform.worldToLocalMatrix * meshNode.localToWorldMatrix;
            if (relative.isIdentity)
            {
                return;
            }

            Debug.LogWarning($"[ObjectDefBuilder] '{meshNode.name}' is offset from the wrapper " +
                             $"(pos {relative.GetPosition()}, rot {relative.rotation.eulerAngles}), so the " +
                             "convex MeshCollider on the wrapper will not line up. Use the BoxBounds " +
                             "collider mode, turn the wrapper off, or fix the model's pivot.");
        }

        private static void WireObjectElement(GameObject root, Rigidbody body, Collider collider)
        {
            Component element = root.AddComponent(SmashMarketBridge.ObjectElementType);
            var serialized = new SerializedObject(element);

            SerializedProperty bodyProperty = serialized.FindProperty(SmashMarketBridge.PropObjectRigidbody);
            if (bodyProperty != null)
            {
                bodyProperty.objectReferenceValue = body;
            }

            SerializedProperty colliderProperty = serialized.FindProperty(SmashMarketBridge.PropObjectCollider);
            if (colliderProperty != null)
            {
                colliderProperty.objectReferenceValue = collider;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
