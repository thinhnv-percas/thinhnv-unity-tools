using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Flattens an object prefab's model hierarchy into one baked mesh asset: the nested FBX nodes are
    /// replaced by a single child of the content root (the wrapper, or the prefab root when there is none)
    /// carrying one MeshFilter + MeshRenderer.
    ///
    /// Worth doing because an unpacked FBX brings its whole node chain along - the shipped
    /// Ice_Cylinder.prefab is five levels deep to reach one mesh - and every one of those transforms is
    /// carried by each pooled instance at runtime.
    ///
    /// A convex MeshCollider on the content root is repointed at the baked mesh, so a multi-part model
    /// stops colliding with only its largest piece.
    /// </summary>
    public static partial class MeshBakeFactory
    {
        /// <summary>Sub-folder the baked mesh assets are written to.</summary>
        public static string MeshFolder(ObjectDefBuildEntry entry) => $"{entry.prefabFolder}/Mesh";

        /// <summary>
        /// Bake <paramref name="prefabAsset"/> in place and return the mesh asset it now uses. Returns null
        /// (with a warning) when the prefab is a variant - a variant inherits its model hierarchy from the
        /// base and cannot delete it, so the base is the thing to bake.
        /// </summary>
        public static Mesh Bake(ObjectDefBuildEntry entry, GameObject prefabAsset)
        {
            if (prefabAsset == null)
            {
                return null;
            }

            string path = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset) != null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{prefabAsset.name}' is a prefab variant: its model " +
                                 "hierarchy is inherited and cannot be replaced. Turn on Bake Base Mesh " +
                                 "instead - the variants pick the baked mesh up from the base.", prefabAsset);
                return null;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform content = ContentRoot(entry, contents.transform);
                if (!TryCombine(content, out Mesh combined, out Material[] materials))
                {
                    Debug.LogWarning($"[ObjectDefBuilder] '{prefabAsset.name}' has no meshes to bake.",
                        prefabAsset);
                    return null;
                }

                Mesh saved = SaveMesh(entry, combined, $"{prefabAsset.name}_Baked");
                ReplaceHierarchy(content, saved, materials);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                return saved;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        /// <summary>
        /// The node the baked mesh lands on: the wrapper when the prefab has one (so it keeps holding the
        /// collider and the visuals together), otherwise the prefab root.
        /// </summary>
        private static Transform ContentRoot(ObjectDefBuildEntry entry, Transform root) =>
            entry.useWrapper && root.childCount > 0 ? root.GetChild(0) : root;

        /// <summary>
        /// Write the combined mesh to <c>&lt;prefabFolder&gt;/Mesh/&lt;name&gt;.asset</c>. An existing asset is
        /// overwritten through <see cref="EditorUtility.CopySerialized"/> so its GUID and file id survive
        /// and anything already referencing it keeps working.
        /// </summary>
        private static Mesh SaveMesh(ObjectDefBuildEntry entry, Mesh combined, string meshName)
        {
            string folder = MeshFolder(entry);
            ToolAssetUtil.EnsureFolder(folder);
            string path = $"{folder}/{meshName}.asset";

            combined.name = meshName;
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(combined, path);
                return combined;
            }

            EditorUtility.CopySerialized(combined, existing);
            existing.name = meshName;
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(combined);
            return existing;
        }

        private const string DefaultModelChildName = "Model";

        /// <summary>What the prefab's convex MeshCollider looked like before the hierarchy was replaced.</summary>
        private struct ColliderInfo
        {
            public bool exists;
            public bool convex;
            public bool onContent;
            public PhysicsMaterial material;
        }

        /// <summary>
        /// Drop the model hierarchy and rebuild it as a single child of the content root carrying the baked
        /// mesh. The renderer goes on that child rather than on the content root itself, so a wrapper stays
        /// what it is - the collider holder - with the visuals underneath it, matching the shipped prefabs.
        /// </summary>
        private static void ReplaceHierarchy(Transform content, Mesh mesh, Material[] materials)
        {
            ColliderInfo collider = CaptureCollider(content);
            string childName = content.childCount > 0 ? content.GetChild(0).name : DefaultModelChildName;

            for (int i = content.childCount - 1; i >= 0; i--)
            {
                Object.DestroyImmediate(content.GetChild(i).gameObject);
            }

            var model = new GameObject(childName);
            model.transform.SetParent(content, false);
            model.layer = content.gameObject.layer;
            model.AddComponent<MeshFilter>().sharedMesh = mesh;
            model.AddComponent<MeshRenderer>().sharedMaterials = materials;

            RestoreCollider(content, collider, mesh);
        }

        /// <summary>
        /// Note the convex MeshCollider wherever it sits. Without a wrapper it lives on the mesh's own node,
        /// which the bake is about to delete - so it has to be re-created rather than just repointed.
        /// </summary>
        private static ColliderInfo CaptureCollider(Transform content)
        {
            MeshCollider existing = content.GetComponentInChildren<MeshCollider>(true);
            if (existing == null)
            {
                return default;
            }

            return new ColliderInfo
            {
                exists = true,
                convex = existing.convex,
                onContent = existing.gameObject == content.gameObject,
                material = existing.sharedMaterial,
            };
        }

        /// <summary>Put the collider back on the content root, pointed at the baked whole.</summary>
        private static void RestoreCollider(Transform content, ColliderInfo info, Mesh mesh)
        {
            if (!info.exists)
            {
                return;
            }

            if (!content.TryGetComponent(out MeshCollider collider))
            {
                collider = content.gameObject.AddComponent<MeshCollider>();
                collider.sharedMaterial = info.material;
            }

            collider.convex = info.convex;
            if (info.convex)
            {
                collider.sharedMesh = mesh;
            }
        }
    }
}
