using System;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// One authored size of an object: the sources the user dragged in plus the assets the last build
    /// produced from them. <see cref="magnitude"/> 1 is the uniform 1x1x1 base, which has no axis variants.
    ///
    /// Per-family base sources: <see cref="modelSource"/>/<see cref="breakSource"/> are the Bar family
    /// base; <see cref="plateModelSource"/>/<see cref="plateBreakSource"/> and
    /// <see cref="cubeModelSource"/>/<see cref="cubeBreakSource"/> are the Plate/Cube family bases.
    /// </summary>
    [Serializable]
    public partial class ObjectDefBuildRow
    {
        public int magnitude = 1;
        public bool include = true;

        [Header("Bar sources (drag here)")]
        public GameObject modelSource;
        public GameObject breakSource;
        public Texture2D modelTexture;
        public Texture2D pieceTexture;

        [Header("Plate sources (dual-axis stretch)")]
        public GameObject plateModelSource;
        public GameObject plateBreakSource;

        [Header("Cube sources (triple-axis stretch)")]
        public GameObject cubeModelSource;
        public GameObject cubeBreakSource;

        [Header("Per-axis models (ModelVariantMode.SeparateModels only)")]
        public GameObject modelSourceX;
        public GameObject modelSourceY;
        public GameObject modelSourceZ;
        public GameObject modelSourceXY;
        public GameObject modelSourceYZ;
        public GameObject modelSourceXZ;
        public GameObject modelSourceXYZ;

        [Header("Last build result (cache)")]
        public Material modelMaterial;
        public Material pieceMaterial;
        public GameObject modelBasePrefab;
        public GameObject plateModelBasePrefab;
        public GameObject cubeModelBasePrefab;
        public GameObject modelPrefabX;
        public GameObject modelPrefabY;
        public GameObject modelPrefabZ;
        public GameObject modelPrefabXY;
        public GameObject modelPrefabYZ;
        public GameObject modelPrefabXZ;
        public GameObject modelPrefabXYZ;
        public GameObject breakBasePrefab;
        public GameObject plateBreakBasePrefab;
        public GameObject cubeBreakBasePrefab;
        public GameObject breakPieceX;
        public GameObject breakPieceY;
        public GameObject breakPieceZ;
        public GameObject breakPieceXY;
        public GameObject breakPieceYZ;
        public GameObject breakPieceXZ;
        public GameObject breakPieceXYZ;

        public bool HasAnySource =>
            modelSource != null || breakSource != null ||
            plateModelSource != null || plateBreakSource != null ||
            cubeModelSource != null || cubeBreakSource != null ||
            modelSourceX != null || modelSourceY != null || modelSourceZ != null ||
            modelSourceXY != null || modelSourceYZ != null || modelSourceXZ != null ||
            modelSourceXYZ != null;

        public bool HasBakeTarget =>
            modelBasePrefab != null || plateModelBasePrefab != null || cubeModelBasePrefab != null ||
            modelPrefabX != null || modelPrefabY != null || modelPrefabZ != null ||
            modelPrefabXY != null || modelPrefabYZ != null || modelPrefabXZ != null ||
            modelPrefabXYZ != null;

        /// <summary>The family base source for Bar/Plate/Cube.</summary>
        public GameObject FamilyModelSource(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => plateModelSource,
            BuildAxisFamily.Cube => cubeModelSource,
            _ => modelSource,
        };

        public GameObject FamilyBreakSource(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => plateBreakSource,
            BuildAxisFamily.Cube => cubeBreakSource,
            _ => breakSource,
        };

        public GameObject FamilyModelBasePrefab(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => plateModelBasePrefab,
            BuildAxisFamily.Cube => cubeModelBasePrefab,
            _ => modelBasePrefab,
        };

        public void SetFamilyModelBasePrefab(BuildAxisFamily family, GameObject prefab)
        {
            switch (family)
            {
                case BuildAxisFamily.Plate: plateModelBasePrefab = prefab; break;
                case BuildAxisFamily.Cube: cubeModelBasePrefab = prefab; break;
                default: modelBasePrefab = prefab; break;
            }
        }

        public GameObject FamilyBreakBasePrefab(BuildAxisFamily family) => family switch
        {
            BuildAxisFamily.Plate => plateBreakBasePrefab,
            BuildAxisFamily.Cube => cubeBreakBasePrefab,
            _ => breakBasePrefab,
        };

        public void SetFamilyBreakBasePrefab(BuildAxisFamily family, GameObject prefab)
        {
            switch (family)
            {
                case BuildAxisFamily.Plate: plateBreakBasePrefab = prefab; break;
                case BuildAxisFamily.Cube: cubeBreakBasePrefab = prefab; break;
                default: breakBasePrefab = prefab; break;
            }
        }
    }
}
