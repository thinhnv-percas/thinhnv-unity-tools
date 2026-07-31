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
    /// How the three axis variants of a stretched model prefab are produced.
    ///
    /// This matters because the level data carries no orientation - every object's "rotation" is zero
    /// across all levels, and <c>SpawnControllerView.SpawnObject</c> applies it as-is - so whatever
    /// orientation a size variant needs has to be baked into its prefab.
    /// </summary>
    public enum ModelVariantMode
    {
        /// <summary>
        /// One base prefab plus three prefab variants of it, each with the model rotated onto its axis
        /// (the same mechanism the *_Break_Piece variants use).
        /// </summary>
        RotateBase = 0,

        /// <summary>
        /// Three independent prefabs, one per axis, built from that axis's own model slot. Slots left
        /// empty fall back to the row's Model, which reproduces the shipped layout of three identical files.
        /// </summary>
        SeparateModels = 1,

        /// <summary>One prefab per magnitude, shared by all three axis rows of the definition.</summary>
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

    /// <summary>
    /// One authored size of an object: the sources the user dragged in plus the assets the last build
    /// produced from them. <see cref="magnitude"/> 1 is the uniform 1x1x1 base, which has no axis variants.
    /// </summary>
    [Serializable]
    public class ObjectDefBuildRow
    {
        public int magnitude = 1;
        public bool include = true;

        [Header("Sources (drag here)")]
        public GameObject modelSource;
        public GameObject breakSource;
        public Texture2D modelTexture;
        public Texture2D pieceTexture;

        [Header("Per-axis models (ModelVariantMode.SeparateModels only)")]
        public GameObject modelSourceX;
        public GameObject modelSourceY;
        public GameObject modelSourceZ;

        [Header("Last build result (cache)")]
        public Material modelMaterial;
        public Material pieceMaterial;
        public GameObject modelBasePrefab;
        public GameObject modelPrefabX;
        public GameObject modelPrefabY;
        public GameObject modelPrefabZ;
        public GameObject breakBasePrefab;
        public GameObject breakPieceX;
        public GameObject breakPieceY;
        public GameObject breakPieceZ;

        public bool HasAnySource =>
            modelSource != null || breakSource != null ||
            modelSourceX != null || modelSourceY != null || modelSourceZ != null;

        /// <summary>
        /// The model dropped on one axis specifically, or null when that axis has none. A non-null value
        /// means "build this axis from its own model" rather than rotating the shared base.
        /// </summary>
        public GameObject PerAxisModelSource(BuildAxis axis) => axis switch
        {
            BuildAxis.X => modelSourceX,
            BuildAxis.Y => modelSourceY,
            BuildAxis.Z => modelSourceZ,
            _ => null,
        };

        public void SetPerAxisModelSource(BuildAxis axis, GameObject source)
        {
            switch (axis)
            {
                case BuildAxis.X: modelSourceX = source; break;
                case BuildAxis.Y: modelSourceY = source; break;
                case BuildAxis.Z: modelSourceZ = source; break;
            }
        }

        /// <summary>True when at least one axis carries its own model.</summary>
        public bool HasPerAxisModels =>
            modelSourceX != null || modelSourceY != null || modelSourceZ != null;

        /// <summary>
        /// The model to build an axis from: that axis's own slot, falling back to the shared
        /// <see cref="modelSource"/> so a half-filled row still produces all three variants.
        /// </summary>
        public GameObject ModelSourceFor(BuildAxis axis)
        {
            GameObject perAxis = PerAxisModelSource(axis);
            return perAxis != null ? perAxis : modelSource;
        }

        /// <summary>Store the prefab built for one axis (X is also the slot used by magnitude 1).</summary>
        public void SetModelPrefab(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.Y: modelPrefabY = prefab; break;
                case BuildAxis.Z: modelPrefabZ = prefab; break;
                default: modelPrefabX = prefab; break;
            }
        }

        public GameObject ModelPrefabFor(BuildAxis axis) => axis switch
        {
            BuildAxis.Y => modelPrefabY,
            BuildAxis.Z => modelPrefabZ,
            _ => modelPrefabX,
        };

        public void SetBreakPiece(BuildAxis axis, GameObject prefab)
        {
            switch (axis)
            {
                case BuildAxis.Y: breakPieceY = prefab; break;
                case BuildAxis.Z: breakPieceZ = prefab; break;
                default: breakPieceX = prefab; break;
            }
        }

        public GameObject BreakPieceFor(BuildAxis axis) => axis switch
        {
            BuildAxis.Y => breakPieceY,
            BuildAxis.Z => breakPieceZ,
            _ => breakPieceX,
        };
    }
}
