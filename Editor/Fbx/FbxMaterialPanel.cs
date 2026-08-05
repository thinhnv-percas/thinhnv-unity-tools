using System;
using System.Linq;
using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.Fbx
{
    /// <summary>
    /// Phase 2 inspector tab: for the selected node, one row per material slot
    /// with a dropdown over every FbxSurfaceMaterial in the scene, plus a
    /// diffuse-texture path field for whichever material is currently assigned.
    /// </summary>
    public class FbxMaterialPanel
    {
        private readonly FbxDocument _document;

        public FbxMaterialPanel(FbxDocument document)
        {
            _document = document;
        }

        public void Draw(FbxNode node)
        {
            if (node == null)
            {
                EditorGUILayout.HelpBox("Select a node to edit its materials.", MessageType.Info);
                return;
            }

            var sceneMaterials = FbxMaterialOperations.GetSceneMaterials(_document.Scene);
            if (sceneMaterials.Count == 0)
            {
                EditorGUILayout.HelpBox("This file has no materials.", MessageType.Info);
                return;
            }

            var slotCount = node.GetMaterialCount();
            if (slotCount == 0)
            {
                EditorGUILayout.HelpBox("This node has no material slots.", MessageType.Info);
                return;
            }

            var names = sceneMaterials.Select(m => m.GetName()).ToArray();

            for (var slot = 0; slot < slotCount; slot++)
            {
                DrawSlot(node, slot, sceneMaterials, names);
                EditorGUILayout.Space();
            }
        }

        private void DrawSlot(FbxNode node, int slot, System.Collections.Generic.List<FbxSurfaceMaterial> sceneMaterials, string[] names)
        {
            EditorGUILayout.LabelField($"Slot {slot}", EditorStyles.boldLabel);

            var current = node.GetMaterial(slot);
            var currentIndex = sceneMaterials.IndexOf(current);

            EditorGUI.BeginChangeCheck();
            var newIndex = EditorGUILayout.Popup("Material", Mathf.Max(currentIndex, 0), names);
            if (EditorGUI.EndChangeCheck() && newIndex != currentIndex)
            {
                var path = FbxNodeOperations.GetNodePath(node) + $" (Material slot {slot})";
                var oldName = current != null ? current.GetName() : "(none)";
                var newMaterial = sceneMaterials[newIndex];
                FbxMaterialOperations.SetMaterialSlot(node, slot, newMaterial);
                _document.RecordChange(FbxChangeKind.MaterialChanged, path, oldName, newMaterial.GetName());
                current = newMaterial;
            }

            var texturePath = FbxMaterialOperations.GetDiffuseTexturePath(current) ?? string.Empty;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Diffuse Texture", GUILayout.Width(110));
            EditorGUILayout.LabelField(string.IsNullOrEmpty(texturePath) ? "(none)" : texturePath);
            if (GUILayout.Button("Browse...", GUILayout.Width(70)))
            {
                var newPath = EditorUtility.OpenFilePanel("Select Texture", "", "png,jpg,jpeg,tga,psd");
                if (!string.IsNullOrEmpty(newPath))
                {
                    var nodePath = FbxNodeOperations.GetNodePath(node) + $" (Slot {slot} Diffuse Texture)";
                    FbxMaterialOperations.SetDiffuseTexturePath(_document.Scene, current, newPath);
                    _document.RecordChange(FbxChangeKind.MaterialChanged, nodePath,
                        string.IsNullOrEmpty(texturePath) ? "(none)" : texturePath, newPath);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
