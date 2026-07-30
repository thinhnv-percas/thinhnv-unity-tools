using System;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The material section: one <see cref="MaterialRecipe"/> for the model and one for the break pieces,
    /// each a template + shader + the texture property that receives the row's texture.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        private static void DrawMaterialSection(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Materials From Texture", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Each size gets its own material: built on the chosen shader, copied from the template " +
                "when one is given so the shader's other settings carry over, with the row's texture " +
                "written into the chosen texture property. A row with no texture keeps the template itself.",
                MessageType.None);

            DrawMaterialRecipe("Model", entry.modelMaterial);
            DrawMaterialRecipe("Piece", entry.pieceMaterial);
        }

        private static void DrawMaterialRecipe(string title, MaterialRecipe recipe)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                recipe.template = (Material)EditorGUILayout.ObjectField(
                    new GUIContent("Template", "Copied as the starting point. Optional."),
                    recipe.template, typeof(Material), false);
                recipe.shader = (Shader)EditorGUILayout.ObjectField(
                    new GUIContent("Shader", "Empty = keep the template's shader."),
                    recipe.shader, typeof(Shader), false);
                recipe.textureProperty = DrawTexturePropertyField(recipe);

                if (!recipe.IsConfigured)
                {
                    EditorGUILayout.HelpBox(
                        "Set a shader or a template, otherwise this size gets no material.",
                        MessageType.Warning);
                }
            }
        }

        /// <summary>
        /// Dropdown over the effective shader's texture properties. Falls back to a free-text field while
        /// no shader is resolvable, and keeps an unrecognised name selectable rather than silently
        /// replacing it.
        /// </summary>
        private static string DrawTexturePropertyField(MaterialRecipe recipe)
        {
            string[] properties = ObjectDefMaterialFactory.TextureProperties(recipe.EffectiveShader);
            if (properties.Length == 0)
            {
                return EditorGUILayout.TextField("Texture Property", recipe.textureProperty);
            }

            var content = new GUIContent("Texture Property", "Texture properties exposed by the shader above.");
            int selected = Array.IndexOf(properties, recipe.textureProperty);
            if (selected >= 0)
            {
                return properties[EditorGUILayout.Popup(content, selected, properties)];
            }

            var withCurrent = new string[properties.Length + 1];
            withCurrent[0] = string.IsNullOrWhiteSpace(recipe.textureProperty)
                ? "(none)"
                : $"{recipe.textureProperty} (not on shader)";
            properties.CopyTo(withCurrent, 1);

            int picked = EditorGUILayout.Popup(content, 0, withCurrent);
            return picked == 0 ? recipe.textureProperty : properties[picked - 1];
        }
    }
}
