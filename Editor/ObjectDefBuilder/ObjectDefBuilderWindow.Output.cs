using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The hierarchy and output section: layers/tag, the rotations, the wrapper child, how axis variants
    /// are produced, and what a rebuild is allowed to overwrite.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        /// <summary>
        /// How the three axis variants of a stretched object prefab are produced, with a line explaining
        /// what the chosen mode writes.
        /// </summary>
        private static void DrawModelVariantMode(ObjectDefBuildEntry entry)
        {
            entry.modelVariantMode = (ModelVariantMode)EditorGUILayout.EnumPopup(
                new GUIContent("Model Variants",
                    "The level data has no rotation, so a stretched object is only oriented correctly if " +
                    "its prefab is. Rotate a shared base, or supply a model per axis."),
                entry.modelVariantMode);

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(" ", ModelVariantHint(entry.modelVariantMode),
                    EditorStyles.miniLabel);
            }
        }

        private static string ModelVariantHint(ModelVariantMode mode) => mode switch
        {
            ModelVariantMode.RotateBase =>
                "Base/<prefix>_2_ModelBase + three variants of it, rotated per axis. " +
                "An axis with its own model in the size row is built from that instead.",
            ModelVariantMode.SeparateModels =>
                "Three prefabs from the row's per-axis model slots (empty = the row's Model).",
            _ => "One prefab per magnitude, shared by all three axis rows (no per-axis models).",
        };

        /// <summary>
        /// The wrapper child toggle plus its name, and a preview of the hierarchy each choice produces so
        /// it is obvious where the collider ends up.
        /// </summary>
        private static void DrawWrapperOption(ObjectDefBuildEntry entry)
        {
            entry.useWrapper = EditorGUILayout.Toggle(
                new GUIContent("Use Wrapper",
                    "Add a child that carries the collider and holds the 3D model, as the shipped prefabs do."),
                entry.useWrapper);

            using (new EditorGUI.IndentLevelScope())
            {
                if (!entry.useWrapper)
                {
                    EditorGUILayout.LabelField(" ", "root (collider on the mesh's own node) -> model",
                        EditorStyles.miniLabel);
                    return;
                }

                entry.wrapperName = EditorGUILayout.TextField("Wrapper Name", entry.wrapperName);
                EditorGUILayout.LabelField(
                    " ", $"root -> {entry.EffectiveWrapperName} (collider + rotation) -> model",
                    EditorStyles.miniLabel);
            }
        }

        /// <summary>
        /// A single wrapper between the shatter root and its piece pivots, mirroring the object prefab's
        /// wrapper. It carries no components, so an axis variant overrides just that one transform.
        /// </summary>
        private static void DrawPieceWrapperOption(ObjectDefBuildEntry entry)
        {
            entry.usePieceWrapper = EditorGUILayout.Toggle(
                new GUIContent("Piece Wrapper",
                    "Put all piece pivots under one wrapper child of the shatter root. No collider - it " +
                    "just groups them, so an axis variant rotates that node instead of every pivot."),
                entry.usePieceWrapper);

            using (new EditorGUI.IndentLevelScope())
            {
                string layout = entry.usePieceWrapper
                    ? $"root -> {entry.EffectiveWrapperName} -> pivot -> piece (mesh + collider + Rigidbody)"
                    : "root -> pivot -> piece (mesh + collider + Rigidbody)";
                EditorGUILayout.LabelField(" ", layout, EditorStyles.miniLabel);
            }
        }

        /// <summary>Collider mode plus its size-rounding toggle, which only applies to box mode.</summary>
        private static void DrawCollider(string label, ref ColliderMode mode, ref bool roundSize)
        {
            mode = (ColliderMode)EditorGUILayout.EnumPopup(label, mode);
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(mode != ColliderMode.BoxBounds))
            {
                roundSize = EditorGUILayout.Toggle(
                    new GUIContent("Round Size To Int",
                        "Snap the fitted box extents to whole units. An extent that would round to 0 " +
                        "keeps its measured value."),
                    roundSize);
            }
        }

        private static void DrawHierarchySection(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Hierarchy & Output", EditorStyles.boldLabel);

            entry.objectTag = EditorGUILayout.TagField("Object Tag", entry.objectTag);
            entry.objectLayer = EditorGUILayout.LayerField("Object Layer", entry.objectLayer);
            entry.pieceLayer = EditorGUILayout.LayerField("Piece Layer", entry.pieceLayer);
            entry.modelRotation = EditorGUILayout.Vector3Field("Model Rotation", entry.modelRotation);
            entry.breakRotation = EditorGUILayout.Vector3Field("Break Rotation", entry.breakRotation);
            entry.unpackModel = EditorGUILayout.Toggle(
                new GUIContent("Unpack Model", "Unpack the FBX inside the prefab, as the shipped prefabs do."),
                entry.unpackModel);

            DrawWrapperOption(entry);
            DrawPieceWrapperOption(entry);
            DrawModelVariantMode(entry);
            entry.breakSlot = (BreakTargetSlot)EditorGUILayout.EnumPopup(
                new GUIContent("Break Slot", "Which BreakEffectGroup slot the pieces prefab is written to."),
                entry.breakSlot);
            entry.overwritePrefabs = EditorGUILayout.Toggle(
                new GUIContent("Overwrite Prefabs",
                    "Off: an existing prefab is kept and only reused, so hand edits survive. " +
                    "Missing prefabs are created either way."),
                entry.overwritePrefabs);

            if (!entry.overwritePrefabs)
            {
                EditorGUILayout.HelpBox(
                    "Existing prefabs will be reused untouched - only missing ones get written. " +
                    "Materials and the definition are still updated.",
                    MessageType.Info);
            }
        }
    }
}
