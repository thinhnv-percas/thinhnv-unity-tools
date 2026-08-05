using System.Collections.Generic;
using System.IO;
using Autodesk.Fbx;

namespace Percas.UnityTools.Fbx
{
    /// <summary>
    /// Phase 2: reassigning which FbxSurfaceMaterial a node's material slot
    /// points to, and which texture file a material's diffuse channel uses.
    /// </summary>
    public static class FbxMaterialOperations
    {
        public static List<FbxSurfaceMaterial> GetSceneMaterials(FbxScene scene)
        {
            var count = scene.GetSrcObjectCount<FbxSurfaceMaterial>();
            var list = new List<FbxSurfaceMaterial>(count);
            for (var i = 0; i < count; i++)
            {
                list.Add(scene.GetSrcObject<FbxSurfaceMaterial>(i));
            }
            return list;
        }

        /// <summary>
        /// FbxNode has no direct "replace slot N" setter, so this rebuilds the
        /// node's material list from scratch with the target slot swapped —
        /// AddMaterial only appends, and there is no SetMaterial(index, ...).
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
                node.RemoveMaterial(node.GetMaterial(0));
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

            var texture = lambert.Diffuse.GetSrcObject<FbxFileTexture>(0);
            return texture?.GetFileName();
        }

        public static void SetDiffuseTexturePath(FbxScene scene, FbxSurfaceMaterial material, string filePath)
        {
            if (!(material is FbxSurfaceLambert lambert))
            {
                return;
            }

            var texture = lambert.Diffuse.GetSrcObject<FbxFileTexture>(0);
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
