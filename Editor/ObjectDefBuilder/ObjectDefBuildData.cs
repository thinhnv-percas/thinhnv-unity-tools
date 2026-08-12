using System;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>How a generated object or break piece is collided.</summary>
    public enum ColliderMode
    {
        None = 0,

        /// <summary>Convex MeshCollider over the mesh (matches the shipped prefabs).</summary>
        MeshConvex = 1,

        /// <summary>BoxCollider fitted to the mesh bounds.</summary>
        BoxBounds = 2,
    }

    /// <summary>Which break slot of the definition the generated pieces prefab is written into.</summary>
    public enum BreakTargetSlot
    {
        Shatter = 0,
        TntShatter = 1,
    }

    /// <summary>
    /// What a build run is allowed to write. Anything switched off keeps whatever the previous build
    /// cached on the row, so a narrowed run (shatter only, materials only) still feeds the definition the
    /// prefabs and materials it already had.
    /// </summary>
    [Flags]
    public enum BuildTargets
    {
        None = 0,
        Materials = 1 << 0,
        BreakPieces = 1 << 1,
        Models = 1 << 2,
        Definition = 1 << 3,

        All = Materials | BreakPieces | Models | Definition,
    }

    /// <summary>
    /// How the axis variants of a stretched model prefab are produced, applied per family.
    ///
    /// Three families exist: Bar (single-axis X/Y/Z, base authored along Y), Plate (dual-axis
    /// XY/YZ/XZ, base authored along YZ), and Cube (triple-axis XYZ, single orientation). Rotation
    /// happens within a family only — a Bar base is never rotated to produce a Plate variant.
    ///
    /// This matters because the level data carries no orientation — every object's "rotation" is zero
    /// across all levels, and <c>SpawnControllerView.SpawnObject</c> applies it as-is — so whatever
    /// orientation a size variant needs has to be baked into its prefab.
    /// </summary>
    public enum ModelVariantMode
    {
        /// <summary>
        /// Per family: one base prefab plus rotated variants. Bar produces X/Y/Z from a Y-authored
        /// base; Plate produces XY/YZ/XZ from a YZ-authored base; Cube produces XYZ with no rotation.
        /// </summary>
        RotateBase = 0,

        /// <summary>
        /// Independent prefabs per axis, each from its own model slot. Slots left empty fall back to
        /// the family's shared base source.
        /// </summary>
        SeparateModels = 1,

        /// <summary>One prefab per magnitude, shared by all axis rows of that family.</summary>
        Shared = 2,
    }

    /// <summary>Which axis-aligned face a decal's local position places it on.</summary>
    public enum DecalDirection
    {
        PosX = 0, NegX = 1, PosY = 2, NegY = 3, PosZ = 4, NegZ = 5,
    }

    /// <summary>
    /// The hand-authored local rotation (Euler) a face's decal is set to when building each of the three
    /// Bar-family axis variants. One instance per face, so the full table is 6 faces x 3 axes.
    /// </summary>
    [Serializable]
    public class DecalFaceRotations
    {
        [Tooltip("This decal's local rotation in the generated X-axis variant.")]
        public Vector3 whenX = Vector3.zero;

        [Tooltip("This decal's local rotation in the generated Y-axis variant.")]
        public Vector3 whenY = Vector3.zero;

        [Tooltip("This decal's local rotation in the generated Z-axis variant.")]
        public Vector3 whenZ = Vector3.zero;

        /// <summary>The authored rotation for <paramref name="axis"/>, or zero for anything but X/Y/Z.</summary>
        public Vector3 For(BuildAxis axis) => axis switch
        {
            BuildAxis.X => whenX,
            BuildAxis.Y => whenY,
            BuildAxis.Z => whenZ,
            _ => Vector3.zero,
        };
    }

    /// <summary>
    /// Per-direction data used to keep a decal submesh readable across axis variants. Each direction
    /// names the one decal object that sits on that face of the base model - cached by
    /// <see cref="AxisVariantFactory.CacheDecalNames"/> right after the base is built from its source FBX,
    /// by checking every decal's local position - and carries the local rotation that decal is set to for
    /// each of the three Bar-family axis variants (see <see cref="DecalFaceRotations"/>), hand-authored
    /// rather than derived, since the whole-model rotation's effect on a decal's own readability isn't
    /// reliably predictable from a single swing/twist formula. Building an axis variant finds each named
    /// decal by name directly, in both the base and the variant, instead of re-detecting its face or
    /// relying on the two hierarchies lining up node-for-node.
    /// </summary>
    [Serializable]
    public class DecalCompensationAxes
    {
        [Tooltip("Name of the decal object on this face, cached from the base model - see " +
                 "AxisVariantFactory.CacheDecalNames. Empty means no decal on that face.")]
        public string name_posX = "Right";
        public string name_negX = "Right1";
        public string name_posY = "Up";
        public string name_negY = "Up1";
        public string name_posZ = "Forward";
        public string name_negZ = "Forward1";

        public DecalFaceRotations posX = new DecalFaceRotations();
        public DecalFaceRotations negX = new DecalFaceRotations();
        public DecalFaceRotations posY = new DecalFaceRotations();
        public DecalFaceRotations negY = new DecalFaceRotations();
        public DecalFaceRotations posZ = new DecalFaceRotations();
        public DecalFaceRotations negZ = new DecalFaceRotations();

        public string Name(DecalDirection direction) => direction switch
        {
            DecalDirection.PosX => name_posX,
            DecalDirection.NegX => name_negX,
            DecalDirection.PosY => name_posY,
            DecalDirection.NegY => name_negY,
            DecalDirection.PosZ => name_posZ,
            _ => name_negZ,
        };

        public void SetName(DecalDirection direction, string value)
        {
            switch (direction)
            {
                case DecalDirection.PosX: name_posX = value; break;
                case DecalDirection.NegX: name_negX = value; break;
                case DecalDirection.PosY: name_posY = value; break;
                case DecalDirection.NegY: name_negY = value; break;
                case DecalDirection.PosZ: name_posZ = value; break;
                default: name_negZ = value; break;
            }
        }

        public DecalFaceRotations Rotations(DecalDirection direction) => direction switch
        {
            DecalDirection.PosX => posX,
            DecalDirection.NegX => negX,
            DecalDirection.PosY => posY,
            DecalDirection.NegY => negY,
            DecalDirection.PosZ => posZ,
            _ => negZ,
        };
    }

    /// <summary>
    /// How a per-size material is produced from a row's texture: a shader to build it on, the texture
    /// property that receives the texture, and an optional template to copy every other property from.
    /// </summary>
    [Serializable]
    public class MaterialRecipe
    {
        [Tooltip("Copied as the starting point so the shader's other properties (matcap, ramp, " +
                 "outline, ...) are preserved. Optional - without it a bare material is created.")]
        public Material template;

        [Tooltip("Shader of the created material. Empty = keep the template's shader.")]
        public Shader shader;

        [Tooltip("Texture property that receives the row's texture, e.g. _BaseMap.")]
        public string textureProperty = "_BaseMap";

        /// <summary>The shader the built material will end up on, or null when nothing is set.</summary>
        public Shader EffectiveShader => shader != null ? shader : template != null ? template.shader : null;

        public bool IsConfigured => shader != null || template != null;
    }
}
