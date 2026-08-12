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
        /// Independent of <see cref="DrawWrapperOption"/>: gives every renderer inside the model its own
        /// pivot parent, the same pivot -&gt; piece pairing the break pieces use.
        /// </summary>
        private static void DrawRendererWrapperOption(ObjectDefBuildEntry entry)
        {
            entry.useRendererWrapper = EditorGUILayout.Toggle(
                new GUIContent("Wrap Renderers",
                    "Insert a pivot parent above every MeshRenderer inside the model, mirroring the break " +
                    "piece's pivot -> piece pairing - one pivot per renderer, carrying its pose so the " +
                    "renderer sits at local identity. Needs Unpack Model on."),
                entry.useRendererWrapper);

            if (entry.useRendererWrapper && !entry.unpackModel)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.HelpBox(
                        "Needs Unpack Model on - skipped otherwise.", MessageType.Warning);
                }
            }
        }

        private static readonly (DecalDirection direction, string label)[] DecalFaces =
        {
            (DecalDirection.PosX, "+X"), (DecalDirection.NegX, "-X"),
            (DecalDirection.PosY, "+Y"), (DecalDirection.NegY, "-Y"),
            (DecalDirection.PosZ, "+Z"), (DecalDirection.NegZ, "-Z"),
        };

        /// <summary>
        /// Holds a copy of one entry's decal names and 6x3 rotation grid so both can be pasted onto
        /// another entry - e.g. Cube C's hand-tuned setup onto Cube A/B.
        /// </summary>
        private static DecalCompensationAxes _decalClipboard;

        /// <summary>A standalone copy of <paramref name="source"/>'s names and rotation grid.</summary>
        private static DecalCompensationAxes CloneDecalAxes(DecalCompensationAxes source)
        {
            var clone = new DecalCompensationAxes();
            PasteDecalAxes(source, clone);
            return clone;
        }

        /// <summary>Copies the decal names and rotation grid from <paramref name="source"/> onto <paramref name="target"/>.</summary>
        private static void PasteDecalAxes(DecalCompensationAxes source, DecalCompensationAxes target)
        {
            target.name_posX = source.name_posX;
            target.name_negX = source.name_negX;
            target.name_posY = source.name_posY;
            target.name_negY = source.name_negY;
            target.name_posZ = source.name_posZ;
            target.name_negZ = source.name_negZ;

            target.posX = new DecalFaceRotations { whenX = source.posX.whenX, whenY = source.posX.whenY, whenZ = source.posX.whenZ };
            target.negX = new DecalFaceRotations { whenX = source.negX.whenX, whenY = source.negX.whenY, whenZ = source.negX.whenZ };
            target.posY = new DecalFaceRotations { whenX = source.posY.whenX, whenY = source.posY.whenY, whenZ = source.posY.whenZ };
            target.negY = new DecalFaceRotations { whenX = source.negY.whenX, whenY = source.negY.whenY, whenZ = source.negY.whenZ };
            target.posZ = new DecalFaceRotations { whenX = source.posZ.whenX, whenY = source.posZ.whenY, whenZ = source.posZ.whenZ };
            target.negZ = new DecalFaceRotations { whenX = source.negZ.whenX, whenY = source.negZ.whenY, whenZ = source.negZ.whenZ };
        }

        /// <summary>
        /// Only meaningful for RotateBase axis variants. Each decal submesh (a MeshRenderer nested under
        /// another MeshRenderer) has its local rotation hand-set per generated axis variant - 6 faces x 3
        /// axes - since the whole-model rotation's effect on a decal's own readability isn't reliably
        /// predictable from a single formula. See <see cref="AxisVariantFactory"/>.
        /// </summary>
        private static void DrawDecalRotationOption(ObjectDefBuildEntry entry)
        {
            entry.keepDecalRotation = EditorGUILayout.Toggle(
                new GUIContent("Keep Decal Rotation",
                    "Set each named decal's local rotation to a hand-authored value per generated axis " +
                    "variant (X/Y/Z), instead of letting it turn fully with the rest of the model. Only " +
                    "applies to RotateBase axis variants."),
                entry.keepDecalRotation);

            if (!entry.keepDecalRotation)
            {
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                DecalCompensationAxes axes = entry.decalCompensationAxes;

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("Decal Names & Rotations", EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Copy", EditorStyles.miniButtonLeft, GUILayout.Width(50)))
                    {
                        _decalClipboard = CloneDecalAxes(axes);
                    }
                    using (new EditorGUI.DisabledScope(_decalClipboard == null))
                    {
                        if (GUILayout.Button("Paste", EditorStyles.miniButtonRight, GUILayout.Width(50)))
                        {
                            PasteDecalAxes(_decalClipboard, axes);
                            GUI.changed = true;
                        }
                    }
                }
                if (_decalClipboard != null)
                {
                    EditorGUILayout.LabelField(" ",
                        "Copy/Paste carries both the decal names and the 6x3 rotation grid - handy for " +
                        "copying a hand-tuned setup from one entry (e.g. Cube C) onto another (Cube A/B).",
                        EditorStyles.miniLabel);
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Decal Names", EditorStyles.miniBoldLabel);
                axes.name_posX = EditorGUILayout.TextField("+X Decal Name", axes.name_posX);
                axes.name_negX = EditorGUILayout.TextField("-X Decal Name", axes.name_negX);
                axes.name_posY = EditorGUILayout.TextField("+Y Decal Name", axes.name_posY);
                axes.name_negY = EditorGUILayout.TextField("-Y Decal Name", axes.name_negY);
                axes.name_posZ = EditorGUILayout.TextField("+Z Decal Name", axes.name_posZ);
                axes.name_negZ = EditorGUILayout.TextField("-Z Decal Name", axes.name_negZ);
                EditorGUILayout.LabelField(" ",
                    "Cached automatically from the base model's decal positions when its base prefab is " +
                    "(re)built. Empty means no decal on that face; edit by hand to override.",
                    EditorStyles.miniLabel);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Decal Rotations (per face x per axis)", EditorStyles.miniBoldLabel);
                foreach ((DecalDirection direction, string label) in DecalFaces)
                {
                    DecalFaceRotations rotations = axes.Rotations(direction);
                    EditorGUILayout.LabelField($"{label} Decal", EditorStyles.miniBoldLabel);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        rotations.whenX = EditorGUILayout.Vector3Field("X Variant", rotations.whenX);
                        rotations.whenY = EditorGUILayout.Vector3Field("Y Variant", rotations.whenY);
                        rotations.whenZ = EditorGUILayout.Vector3Field("Z Variant", rotations.whenZ);
                    }
                }
                EditorGUILayout.LabelField(" ",
                    "Each field is the decal's absolute local rotation (Euler) in that generated axis " +
                    "variant's prefab - not an offset. Zero means identity, not \"leave untouched\".",
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

        private static readonly BuildAxis[] BarAxisChoices = { BuildAxis.X, BuildAxis.Y, BuildAxis.Z };
        private static readonly string[] BarAxisLabels = { "X", "Y", "Z" };

        /// <summary>
        /// Which single axis the Bar family's source model is actually authored along, restricted to
        /// X/Y/Z since the other <see cref="BuildAxis"/> values do not apply to a single-axis model.
        /// </summary>
        private static void DrawBarAuthoredAxis(ObjectDefBuildEntry entry)
        {
            int index = System.Array.IndexOf(BarAxisChoices, entry.barAuthoredAxis);
            index = EditorGUILayout.Popup(
                new GUIContent("Bar Authored Axis",
                    "The axis the Bar family's source model (and break source) is actually authored " +
                    "along. Default Y matches the existing convention - change this only if your FBX's " +
                    "long dimension runs along X or Z instead, so axis variants still rotate onto the " +
                    "right axis. Bar family only."),
                Mathf.Max(index, 0), BarAxisLabels);
            entry.barAuthoredAxis = BarAxisChoices[index];
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
            DrawBarAuthoredAxis(entry);
            entry.unpackModel = EditorGUILayout.Toggle(
                new GUIContent("Unpack Model", "Unpack the FBX inside the prefab, as the shipped prefabs do."),
                entry.unpackModel);

            DrawWrapperOption(entry);
            DrawRendererWrapperOption(entry);
            DrawPieceWrapperOption(entry);
            DrawModelVariantMode(entry);
            DrawDecalRotationOption(entry);
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
