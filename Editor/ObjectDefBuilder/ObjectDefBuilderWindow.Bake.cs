using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Baking as a standalone action: flatten already-built prefabs into baked meshes without a full
    /// rebuild. Works off the prefabs the cache points at, regardless of the bake toggles.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        private void RequestBake(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            EditorApplication.delayCall += () =>
            {
                BakeEntry(entry, onlyMagnitude);
                Repaint();
            };
        }

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

        private void BakeRow(ObjectDefBuildEntry entry, ObjectDefBuildRow row, ref int baked, ref int skipped)
        {
            if (row.magnitude > 1 && entry.bakeMeshPerAxis)
            {
                BakeAllFamilies(entry, row, ref baked, ref skipped);
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

        private void BakeAllFamilies(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            ref int baked, ref int skipped)
        {
            foreach (BuildAxisFamily family in AllFamilies)
            {
                GameObject basePrefab = row.FamilyModelBasePrefab(family);
                if (basePrefab == null)
                {
                    continue;
                }

                Mesh baseMesh = null;
                bool scratchMesh = false;

                if (entry.bakeBaseMesh)
                {
                    baseMesh = MeshBakeFactory.Bake(entry, basePrefab);
                    if (baseMesh != null)
                    {
                        Track(lastBuilt, basePrefab);
                        baked++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    baseMesh = MeshBakeFactory.CombineFrom(entry, basePrefab);
                    scratchMesh = baseMesh != null;
                }

                try
                {
                    if (baseMesh != null)
                    {
                        BakeAxes(entry, row, baseMesh, BuildAxisExtensions.FamilyAxes(family), ref baked);
                    }
                }
                finally
                {
                    if (scratchMesh)
                    {
                        Object.DestroyImmediate(baseMesh);
                    }
                }
            }
        }

        private void BakeAxes(ObjectDefBuildEntry entry, ObjectDefBuildRow row, Mesh baseMesh,
            BuildAxis[] axes, ref int baked)
        {
            foreach (BuildAxis axis in axes)
            {
                GameObject variant = row.ModelPrefabFor(axis);
                if (variant != null && MeshBakeFactory.BakeAxis(entry, row, variant, baseMesh, axis))
                {
                    Track(lastBuilt, variant);
                    baked++;
                }
            }
        }

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
