using System.Collections.Generic;
using System.IO;
using Autodesk.Fbx;

namespace Thinhnv.UnityTools.Fbx
{
    /// <summary>
    /// Phase 2: reassigning which FbxSurfaceMaterial a node's material slot
    /// points to, and which texture file a material's diffuse channel uses.
    ///
    /// GetSrcObjectCount()/GetSrcObject(int) have no generic &lt;T&gt; overload in
    /// this SDK binding — they return the untyped connection list, so every
    /// lookup here filters the result with `is`/`as` instead.
    /// </summary>
    public static class FbxMaterialOperations
    {
        public static List<FbxSurfaceMaterial> GetSceneMaterials(FbxScene scene)
        {
            var list = new List<FbxSurfaceMaterial>();
            var count = scene.GetSrcObjectCount();
            for (var i = 0; i < count; i++)
            {
                if (scene.GetSrcObject(i) is FbxSurfaceMaterial material)
                {
                    list.Add(material);
                }
            }
            return list;
        }

        /// <summary>
        /// FbxNode has no direct "replace slot N" setter and no RemoveMaterial
        /// convenience method, so this rebuilds the node's material list from
        /// scratch: disconnect everything via the generic object connection
        /// (DisconnectSrcObject, the same connection AddMaterial creates), then
        /// re-add in order with the target slot swapped.
        /// </summary>
        public static void SetMaterialSlot(FbxNode node, int slotIndex, FbxSurfaceMaterial newMaterial)
        {
            var count = node.GetMaterialCount();
            var materials = new FbxSurfaceMaterial[count];
            for (var i = 0; i < count; i++)
            {
                materials[i] = node.GetMaterial(i);
            }

            materials[slotIndex] = newMaterial;

            while (node.GetMaterialCount() > 0)
            {
                node.DisconnectSrcObject(node.GetMaterial(0));
            }

            foreach (var material in materials)
            {
                node.AddMaterial(material);
            }
        }

        /// <summary>
        /// FbxSurfacePhong derives from FbxSurfaceLambert, so casting to Lambert
        /// covers the Diffuse channel for both of the two material types the
        /// FBX SDK actually creates on import.
        /// </summary>
        public static string GetDiffuseTexturePath(FbxSurfaceMaterial material)
        {
            if (!(material is FbxSurfaceLambert lambert))
            {
                return null;
            }

            if (lambert.Diffuse.GetSrcObjectCount() == 0)
            {
                return null;
            }

            var texture = lambert.Diffuse.GetSrcObject(0) as FbxFileTexture;
            return texture?.GetFileName();
        }

        public static void SetDiffuseTexturePath(FbxScene scene, FbxSurfaceMaterial material, string filePath)
        {
            if (!(material is FbxSurfaceLambert lambert))
            {
                return;
            }

            var texture = lambert.Diffuse.GetSrcObjectCount() > 0
                ? lambert.Diffuse.GetSrcObject(0) as FbxFileTexture
                : null;

            if (texture == null)
            {
                texture = FbxFileTexture.Create(scene, material.GetName() + "_DiffuseTexture");
                lambert.Diffuse.ConnectSrcObject(texture);
            }

            texture.SetFileName(filePath);
            texture.SetRelativeFileName(Path.GetFileName(filePath));
        }
    }
}
