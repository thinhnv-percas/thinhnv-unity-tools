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
