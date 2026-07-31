using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Flattens an object prefab's model hierarchy into one baked mesh asset: the nested FBX nodes are
    /// replaced by a single MeshFilter + MeshRenderer on the content root (the wrapper, or the prefab root
    /// when there is none).
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
        /// Bake <paramref name="prefabAsset"/> in place. Returns false (with a warning) when the prefab is
        /// a variant - a variant inherits its model hierarchy from the base and cannot delete it, so the
        /// base is the thing to bake.
        /// </summary>
        public static bool Bake(ObjectDefBuildEntry entry, GameObject prefabAsset)
        {
            if (prefabAsset == null)
            {
                return false;
            }

            string path = AssetDatabase.GetAssetPath(prefabAsset);
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            if (PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset) != null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{prefabAsset.name}' is a prefab variant: its model " +
                                 "hierarchy is inherited and cannot be replaced. Turn on Bake Base Mesh " +
                                 "instead - the variants pick the baked mesh up from the base.", prefabAsset);
                return false;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Transform content = ContentRoot(entry, contents.transform);
                if (!TryCombine(content, out Mesh combined, out Material[] materials))
                {
                    Debug.LogWarning($"[ObjectDefBuilder] '{prefabAsset.name}' has no meshes to bake.",
                        prefabAsset);
                    return false;
                }

                Mesh saved = SaveMesh(entry, combined, $"{prefabAsset.name}_Baked");
                ReplaceHierarchy(content, saved, materials);
                PrefabUtility.SaveAsPrefabAsset(contents, path);
                return true;
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

        /// <summary>
        /// Drop the model hierarchy and put the baked mesh straight on the content root, keeping whatever
        /// components (collider, and on a wrapper-less prefab the Rigidbody / ObjectElement) already live there.
        /// </summary>
        private static void ReplaceHierarchy(Transform content, Mesh mesh, Material[] materials)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                Object.DestroyImmediate(content.GetChild(i).gameObject);
            }

            if (!content.TryGetComponent(out MeshFilter filter))
            {
                filter = content.gameObject.AddComponent<MeshFilter>();
            }

            filter.sharedMesh = mesh;

            if (!content.TryGetComponent(out MeshRenderer renderer))
            {
                renderer = content.gameObject.AddComponent<MeshRenderer>();
            }

            renderer.sharedMaterials = materials;

            // The convex hull was built from one source mesh; point it at the baked whole instead.
            if (content.TryGetComponent(out MeshCollider collider) && collider.convex)
            {
                collider.sharedMesh = mesh;
            }
        }
    }
}
