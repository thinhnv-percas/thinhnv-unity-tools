using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Combining a model hierarchy into one mesh: every sub-mesh is baked into the content root's local
    /// space, grouped by material so the result keeps one sub-mesh per distinct material.
    /// </summary>
    public static partial class MeshBakeFactory
    {
        /// <summary>
        /// Merge every mesh under <paramref name="content"/> into a single mesh expressed in that node's
        /// local space. <paramref name="materials"/> comes back ordered to match the result's sub-meshes.
        /// Returns false when there is nothing to bake.
        /// </summary>
        private static bool TryCombine(Transform content, out Mesh mesh, out Material[] materials)
        {
            mesh = null;
            materials = null;

            var groupMaterials = new List<Material>();
            var groupInstances = new List<List<CombineInstance>>();
            Matrix4x4 toContent = content.worldToLocalMatrix;

            foreach (MeshFilter filter in content.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null)
                {
                    continue;
                }

                filter.TryGetComponent(out MeshRenderer renderer);
                Matrix4x4 transform = toContent * filter.transform.localToWorldMatrix;

                for (int sub = 0; sub < filter.sharedMesh.subMeshCount; sub++)
                {
                    Material material = MaterialAt(renderer, sub);
                    int group = IndexOfMaterial(groupMaterials, material);
                    if (group < 0)
                    {
                        group = groupMaterials.Count;
                        groupMaterials.Add(material);
                        groupInstances.Add(new List<CombineInstance>());
                    }

                    groupInstances[group].Add(new CombineInstance
                    {
                        mesh = filter.sharedMesh, subMeshIndex = sub, transform = transform,
                    });
                }
            }

            if (groupMaterials.Count == 0)
            {
                return false;
            }

            mesh = CombineGroups(groupInstances);
            materials = groupMaterials.ToArray();
            return true;
        }

        /// <summary>
        /// Merge each material group into one mesh (baking transforms), then merge the groups without
        /// merging sub-meshes, so the final mesh has one sub-mesh per material in the same order.
        /// </summary>
        private static Mesh CombineGroups(List<List<CombineInstance>> groupInstances)
        {
            var perGroup = new CombineInstance[groupInstances.Count];
            for (int i = 0; i < groupInstances.Count; i++)
            {
                var groupMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                groupMesh.CombineMeshes(groupInstances[i].ToArray(), true, true);
                perGroup[i] = new CombineInstance { mesh = groupMesh, transform = Matrix4x4.identity };
            }

            var combined = new Mesh { indexFormat = IndexFormat.UInt32 };
            combined.CombineMeshes(perGroup, false, false);
            combined.RecalculateBounds();

            // The per-group meshes were scratch; only the combined result becomes an asset.
            foreach (CombineInstance instance in perGroup)
            {
                Object.DestroyImmediate(instance.mesh);
            }

            return combined;
        }

        private static Material MaterialAt(MeshRenderer renderer, int subMesh)
        {
            if (renderer == null)
            {
                return null;
            }

            Material[] materials = renderer.sharedMaterials;
            return subMesh < materials.Length ? materials[subMesh] : null;
        }

        /// <summary>List lookup rather than a Dictionary because a null material is a valid group key.</summary>
        private static int IndexOfMaterial(List<Material> materials, Material material)
        {
            for (int i = 0; i < materials.Count; i++)
            {
                if (materials[i] == material)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
