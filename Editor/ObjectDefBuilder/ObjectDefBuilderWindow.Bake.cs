using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Baking as an action rather than a build-time flag: the `Bake Base Mesh` / `Bake Size Mesh` toggles
    /// only fire while prefabs are being written, so prefabs built before baking existed - or before those
    /// toggles were switched on - had no way to be flattened short of a full rebuild.
    ///
    /// These buttons work off the prefabs the cache already points at, and bake everything that can be
    /// baked regardless of the toggles. Variants are skipped by <see cref="MeshBakeFactory"/> with a
    /// warning, since a variant cannot replace the hierarchy it inherits.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        /// <summary>Queue a bake for after this OnGUI pass, for the same reason builds are queued.</summary>
        private void RequestBake(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            EditorApplication.delayCall += () =>
            {
                BakeEntry(entry, onlyMagnitude);
                Repaint();
            };
        }

        /// <summary>Bake every included size, or just <paramref name="onlyMagnitude"/> when non-zero.</summary>
        private void BakeEntry(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            lastBuilt.Clear();
            lastBuiltFolder = entry.prefabFolder;

            int baked = 0;
            int skipped = 0;

            foreach (ObjectDefBuildRow row in entry.rows)
            {
                if (!row.include || !row.HasBakeTarget)
                {
                    continue;
                }

                if (onlyMagnitude > 0 && row.magnitude != onlyMagnitude)
                {
                    continue;
                }

                BakeRow(entry, row, ref baked, ref skipped);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string scope = onlyMagnitude > 0 ? $"size {onlyMagnitude}" : "all sizes";
            string skippedNote = skipped > 0
                ? $" {skipped} skipped (variants are baked through their base - see the Console)."
                : string.Empty;

            status = baked == 0
                ? $"Nothing baked for {scope}.{skippedNote}"
                : $"Baked {baked} prefab(s) for {scope}.{skippedNote}";
            Debug.Log($"[ObjectDefBuilder] {status}");
        }

        /// <summary>
        /// Bake one row: the model base first, then either a per-axis mesh for each variant (derived from
        /// the base's freshly baked mesh) or the size prefabs themselves.
        /// </summary>
        private void BakeRow(ObjectDefBuildEntry entry, ObjectDefBuildRow row, ref int baked, ref int skipped)
        {
            Mesh baseMesh = null;
            bool scratchMesh = false;

            if (row.modelBasePrefab != null && entry.bakeBaseMesh)
            {
                baseMesh = MeshBakeFactory.Bake(entry, row.modelBasePrefab);
                if (baseMesh != null)
                {
                    Track(lastBuilt, row.modelBasePrefab);
                    baked++;
                }
                else
                {
                    skipped++;
                }
            }
            else if (row.modelBasePrefab != null && entry.bakeMeshPerAxis)
            {
                // Per-axis without baking the base: read its geometry, leave the asset alone.
                baseMesh = MeshBakeFactory.CombineFrom(entry, row.modelBasePrefab);
                scratchMesh = baseMesh != null;
            }

            try
            {
                if (entry.bakeMeshPerAxis && baseMesh != null && row.magnitude > 1)
                {
                    BakeAxes(entry, row, baseMesh, ref baked);
                    return;
                }

                foreach (GameObject prefab in SizePrefabs(row))
                {
                    if (MeshBakeFactory.Bake(entry, prefab) != null)
                    {
                        Track(lastBuilt, prefab);
                        baked++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            finally
            {
                // CombineFrom hands back a scratch mesh, not an asset - release it on every path out.
                if (scratchMesh)
                {
                    Object.DestroyImmediate(baseMesh);
                }
            }
        }

        private void BakeAxes(ObjectDefBuildEntry entry, ObjectDefBuildRow row, Mesh baseMesh, ref int baked)
        {
            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                GameObject variant = row.ModelPrefabFor(axis);
                if (variant != null && MeshBakeFactory.BakeAxis(entry, row, variant, baseMesh, axis))
                {
                    Track(lastBuilt, variant);
                    baked++;
                }
            }
        }

        /// <summary>
        /// A row's size prefabs, de-duplicated because Shared mode points all three axes at one prefab.
        /// </summary>
        private static IEnumerable<GameObject> SizePrefabs(ObjectDefBuildRow row)
        {
            var seen = new List<GameObject>();

            if (row.magnitude <= 1)
            {
                Add(seen, row.ModelPrefabFor(BuildAxis.None));
                return seen;
            }

            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                Add(seen, row.ModelPrefabFor(axis));
            }

            return seen;
        }

        private static void Add(List<GameObject> list, GameObject prefab)
        {
            if (prefab != null && !list.Contains(prefab))
            {
                list.Add(prefab);
            }
        }
    }
}
