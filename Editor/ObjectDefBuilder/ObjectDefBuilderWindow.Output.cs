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
                "Per family: a base prefab + rotated variants (Bar: X/Y/Z, Plate: XY/YZ/XZ, Cube: XYZ). " +
                "An axis with its own model slot is built from that instead.",
            ModelVariantMode.SeparateModels =>
                "Independent prefabs per axis, each from its own model slot (empty = family base).",
            _ => "One prefab per magnitude per family, shared by all axes of that family.",
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
        /// Mesh baking, split the way the prefabs are: the shared base on its own toggle, the size
        /// prefabs (uniform 1x1 and each axis prefab) on another.
        /// </summary>
        private static void DrawMeshBakeOptions(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(2);
            entry.bakeBaseMesh = EditorGUILayout.Toggle(
                new GUIContent("Bake Base Mesh",
                    "Flatten the *_ModelBase model hierarchy into one baked mesh asset."),
                entry.bakeBaseMesh);
            entry.bakeSizeMesh = EditorGUILayout.Toggle(
                new GUIContent("Bake Size Mesh",
                    "Flatten the uniform 1x1 prefab and each axis prefab that is not a variant."),
                entry.bakeSizeMesh);

            entry.bakeMeshPerAxis = EditorGUILayout.Toggle(
                new GUIContent("Mesh Per Axis",
                    "Give each axis variant its own mesh with the rotation baked into the vertices, " +
                    "instead of all three sharing the base's mesh and rotating their wrapper. " +
                    "With Bake Base Mesh off, the base prefab is only read - never modified."),
                entry.bakeMeshPerAxis);

            if (!entry.bakeBaseMesh && !entry.bakeSizeMesh && !entry.bakeMeshPerAxis)
            {
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField(" ", $"meshes -> {MeshBakeFactory.MeshFolder(entry)}",
                    EditorStyles.miniLabel);
            }

            if (entry.bakeMeshPerAxis)
            {
                EditorGUILayout.HelpBox(
                    entry.bakeBaseMesh
                        ? "Each axis variant gets '<prefix>_<n><axis>_Baked' with the rotation in its " +
                          "vertices, and the base is flattened too. Applies to RotateBase mode; the other " +
                          "modes already write one prefab (and one mesh) per axis."
                        : "Base prefab left untouched: it is only read for its geometry. Each axis variant " +
                          "gets its own baked mesh, overriding the mesh on the first mesh node and " +
                          "disabling the other renderers - a variant cannot delete inherited nodes, so its " +
                          "hierarchy stays as the base authored it.",
                    MessageType.None);
            }

            if (entry.bakeSizeMesh && entry.modelVariantMode == ModelVariantMode.RotateBase)
            {
                EditorGUILayout.HelpBox(
                    "In RotateBase mode the axis prefabs are variants of the base, and a variant cannot " +
                    "delete the hierarchy it inherits - those are skipped with a warning. Use Bake Base " +
                    "Mesh instead; the variants pick the baked mesh up from the base. Bake Size Mesh " +
                    "still applies to the uniform 1x1 prefab and to any axis with its own model.",
                    MessageType.Info);
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
            DrawMeshBakeOptions(entry);
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
