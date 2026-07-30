using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Turns a dragged texture into a material asset following a <see cref="MaterialRecipe"/>: build on
    /// the chosen shader, copy a template when one is given so the shader's other properties (matcap,
    /// ramp, outline, ...) carry over, then write the texture into the named texture property.
    /// </summary>
    public static class ObjectDefMaterialFactory
    {
        /// <summary>Fallbacks tried when the recipe names no property, or names one the shader lacks.</summary>
        private static readonly string[] FallbackProperties = { "_BaseMap", "_MainTex", "_BaseColorMap" };

        /// <summary>
        /// Load-or-create <paramref name="folder"/>/<paramref name="materialName"/>.mat from
        /// <paramref name="recipe"/> with <paramref name="texture"/> in its texture property.
        /// Returns the template itself when there is no texture to bake, or null when the recipe is empty.
        /// </summary>
        public static Material CreateOrUpdate(string folder, string materialName,
            MaterialRecipe recipe, Texture2D texture)
        {
            if (texture == null)
            {
                return recipe?.template;
            }

            if (recipe == null || !recipe.IsConfigured)
            {
                Debug.LogWarning($"[ObjectDefBuilder] No shader or template set; texture '{texture.name}' " +
                                 "cannot be turned into a material.");
                return null;
            }

            ToolAssetUtil.EnsureFolder(folder);
            string path = $"{folder}/{materialName}.mat";

            Material material = AssetDatabase.LoadAssetAtPath<Material>(path)
                                ?? CreateAsset(recipe, materialName, path);

            Shader shader = recipe.EffectiveShader;
            if (shader != null && material.shader != shader)
            {
                material.shader = shader;
            }

            AssignTexture(material, recipe.textureProperty, texture, materialName);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material CreateAsset(MaterialRecipe recipe, string materialName, string path)
        {
            Material material = recipe.template != null
                ? Object.Instantiate(recipe.template)
                : new Material(recipe.shader);
            material.name = materialName;
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        /// <summary>Write the texture into the recipe's property, falling back to the usual base-map names.</summary>
        private static void AssignTexture(Material material, string property, Texture2D texture, string materialName)
        {
            if (!string.IsNullOrWhiteSpace(property) && material.HasProperty(property))
            {
                material.SetTexture(property, texture);
                return;
            }

            if (!string.IsNullOrWhiteSpace(property))
            {
                Debug.LogWarning($"[ObjectDefBuilder] Shader '{material.shader.name}' has no texture " +
                                 $"property '{property}'; trying the usual base-map names for '{materialName}'.");
            }

            foreach (string fallback in FallbackProperties)
            {
                if (material.HasProperty(fallback))
                {
                    material.SetTexture(fallback, texture);
                    return;
                }
            }

            Debug.LogWarning($"[ObjectDefBuilder] '{materialName}' got no texture: shader " +
                             $"'{material.shader.name}' exposes none of {string.Join("/", FallbackProperties)}.");
        }

        /// <summary>Every texture property a shader exposes, for the property dropdown in the window.</summary>
        public static string[] TextureProperties(Shader shader)
        {
            if (shader == null)
            {
                return System.Array.Empty<string>();
            }

            var names = new List<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    names.Add(shader.GetPropertyName(i));
                }
            }

            return names.ToArray();
        }

        /// <summary>Assign <paramref name="material"/> to every MeshRenderer of a hierarchy (all sub-meshes).</summary>
        public static void ApplyToRenderers(GameObject root, Material material)
        {
            if (material == null)
            {
                return;
            }

            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var materials = new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
                for (int i = 0; i < materials.Length; i++)
                {
                    materials[i] = material;
                }

                renderer.sharedMaterials = materials;
            }
        }
    }
}
