using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>The per-entry settings block: target definition, naming, materials, physics, hierarchy.</summary>
    public partial class ObjectDefBuilderWindow
    {
        private void DrawEntrySettings(ObjectDefBuildEntry entry)
        {
            entry.label = EditorGUILayout.TextField("Entry Name", entry.label);

            showSettings = EditorGUILayout.Foldout(showSettings, "Settings", true, EditorStyles.foldoutHeader);
            if (!showSettings)
            {
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                DrawTargetSection(entry);
                DrawNamingSection(entry);
                DrawMaterialSection(entry);
                DrawPhysicsSection(entry);
                DrawHierarchySection(entry);
            }
        }

        private void DrawTargetSection(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);

            entry.objectTypeName = DrawObjectTypePopup(entry.objectTypeName);

            using (new EditorGUILayout.HorizontalScope())
            {
                entry.definition = (ScriptableObject)EditorGUILayout.ObjectField(
                    "Definition", entry.definition, SmashMarketBridge.ObjectDefinitionType, false);

                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(entry.objectTypeName)))
                {
                    if (GUILayout.Button("Load/Create", GUILayout.Width(90)))
                    {
                        entry.definition = ObjectDefinitionWriter.LoadOrCreate(
                            entry.definitionFolder, entry.objectTypeName);
                        AssetDatabase.SaveAssets();
                    }
                }
            }

            entry.definitionFolder = EditorGUILayout.TextField("Definition Folder", entry.definitionFolder);

            using (new EditorGUI.DisabledScope(entry.definition == null))
            {
                var fill = new GUIContent("Fill Cache From Definition",
                    "Copy the definition's model and shatter prefabs back into this entry's size rows, so " +
                    "the tool can bake, select and partially rebuild prefabs it did not create. " +
                    "Source models and textures are not stored in a definition and are left alone.");
                if (GUILayout.Button(fill))
                {
                    FillFromDefinition(entry);
                }
            }
        }

        /// <summary>Popup over the game's ObjectType enum names, with an explicit "(none)" entry.</summary>
        private static string DrawObjectTypePopup(string current)
        {
            string[] names = SmashMarketBridge.ObjectTypeNames();
            var options = new string[names.Length + 1];
            options[0] = "(none)";
            names.CopyTo(options, 1);

            int selected = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == current)
                {
                    selected = i + 1;
                    break;
                }
            }

            selected = EditorGUILayout.Popup("Object Type", selected, options);
            return selected <= 0 ? string.Empty : names[selected - 1];
        }

        private static void DrawNamingSection(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Naming & Folders", EditorStyles.boldLabel);

            entry.modelPrefix = EditorGUILayout.TextField(
                new GUIContent("Model Prefix", "'Ice_Cylinder' -> Ice_Cylinder, Ice_Cylinder_2x, ..."),
                entry.modelPrefix);
            entry.breakPrefix = EditorGUILayout.TextField(
                new GUIContent("Break Prefix", "'IceCircle' -> IceCircle_2x_Break_Piece. Empty = model prefix."),
                entry.breakPrefix);
            entry.prefabFolder = EditorGUILayout.TextField("Prefab Folder", entry.prefabFolder);
            entry.materialFolder = EditorGUILayout.TextField("Material Folder", entry.materialFolder);
        }

        private static void DrawPhysicsSection(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Physics", EditorStyles.boldLabel);

            DrawCollider("Object Collider", ref entry.objectColliderMode,
                ref entry.roundObjectColliderSize);
            DrawCollider("Piece Collider", ref entry.pieceColliderMode,
                ref entry.roundPieceColliderSize);

            entry.objectPhysicMaterial = (PhysicsMaterial)EditorGUILayout.ObjectField(
                "Object Physics Mat", entry.objectPhysicMaterial, typeof(PhysicsMaterial), false);
            entry.piecePhysicMaterial = (PhysicsMaterial)EditorGUILayout.ObjectField(
                "Piece Physics Mat", entry.piecePhysicMaterial, typeof(PhysicsMaterial), false);
            entry.objectMass = EditorGUILayout.FloatField("Object Mass", entry.objectMass);
            entry.pieceMass = EditorGUILayout.FloatField("Piece Mass", entry.pieceMass);
        }
    }
}
