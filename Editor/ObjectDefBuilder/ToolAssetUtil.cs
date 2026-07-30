using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>Small AssetDatabase helpers shared by the Object Definition Builder factories.</summary>
    public static class ToolAssetUtil
    {
        /// <summary>Create <paramref name="folder"/> and every missing parent below "Assets".</summary>
        public static void EnsureFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder))
            {
                return;
            }

            string[] parts = folder.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        /// <summary>Set <paramref name="layer"/> on a hierarchy, root included.</summary>
        public static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            foreach (Transform child in root)
            {
                SetLayerRecursively(child, layer);
            }
        }

        /// <summary>
        /// Assign <paramref name="tag"/> if it exists in the project, otherwise warn and leave the
        /// GameObject untagged (assigning an undefined tag throws).
        /// </summary>
        public static void TrySetTag(GameObject go, string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag == "Untagged")
            {
                return;
            }

            foreach (string existing in UnityEditorInternal.InternalEditorUtility.tags)
            {
                if (existing == tag)
                {
                    go.tag = tag;
                    return;
                }
            }

            Debug.LogWarning($"[ObjectDefBuilder] Tag '{tag}' does not exist; '{go.name}' left untagged.");
        }

        /// <summary>The renderer with the largest bounds volume under <paramref name="root"/>, or null.</summary>
        public static MeshRenderer LargestRenderer(GameObject root)
        {
            MeshRenderer best = null;
            float bestVolume = -1f;

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Vector3 size = renderer.bounds.size;
                float volume = size.x * size.y * size.z;
                if (volume > bestVolume)
                {
                    bestVolume = volume;
                    best = renderer;
                }
            }

            return best;
        }

        /// <summary>
        /// Combined mesh bounds of a hierarchy, expressed in <paramref name="root"/>'s local space.
        ///
        /// Works from each mesh's own bounds corners rather than the renderers' world AABBs, so a rotated
        /// root (the object prefab's wrapper carries the model rotation) still gets a tight box instead of
        /// an inflated one.
        /// </summary>
        public static bool TryLocalBounds(GameObject root, out Bounds local)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            bool any = false;
            local = default;

            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                Matrix4x4 relative = toRoot * filter.transform.localToWorldMatrix;
                Bounds mesh = filter.sharedMesh.bounds;

                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = relative.MultiplyPoint3x4(Corner(mesh, corner));
                    if (any)
                    {
                        local.Encapsulate(point);
                    }
                    else
                    {
                        local = new Bounds(point, Vector3.zero);
                        any = true;
                    }
                }
            }

            return any;
        }

        /// <summary>One of the 8 corners of a Bounds, selected by the low three bits of <paramref name="index"/>.</summary>
        private static Vector3 Corner(Bounds bounds, int index) => new Vector3(
            (index & 1) == 0 ? bounds.min.x : bounds.max.x,
            (index & 2) == 0 ? bounds.min.y : bounds.max.y,
            (index & 4) == 0 ? bounds.min.z : bounds.max.z);
    }
}
