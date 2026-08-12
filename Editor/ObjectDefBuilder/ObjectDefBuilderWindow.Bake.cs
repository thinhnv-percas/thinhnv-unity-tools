using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Baking as a standalone action: run <c>ObjectElement.BakeMeshIntoWrapper()</c> (via
    /// <see cref="SmashMarketBridge"/>) over every already-built prefab a row points at, without a full
    /// rebuild. Works off the prefabs the cache points at - the base per family and every axis variant,
    /// each baked independently since ObjectElement's own bake handles variants directly.
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
                ? $" {skipped} skipped (no ObjectElement on the prefab)."
                : string.Empty;

            status = baked == 0
                ? $"Nothing baked for {scope}.{skippedNote}"
                : $"Baked {baked} prefab(s) for {scope}.{skippedNote}";
            Debug.Log($"[ObjectDefBuilder] {status}");
        }

        private void BakeRow(ObjectDefBuildEntry entry, ObjectDefBuildRow row, ref int baked, ref int skipped)
        {
            foreach (GameObject prefab in RowPrefabs(entry, row))
            {
                if (SmashMarketBridge.BakeMeshIntoWrapper(prefab))
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

        /// <summary>
        /// Every prefab this row currently has built: the uniform, each axis variant, and - when
        /// <see cref="ObjectDefBuildEntry.bakeBaseModel"/> is on - each family's base prefab too.
        /// </summary>
        private static IEnumerable<GameObject> RowPrefabs(ObjectDefBuildEntry entry, ObjectDefBuildRow row)
        {
            var seen = new List<GameObject>();

            Add(seen, row.ModelPrefabFor(BuildAxis.None));

            if (entry.bakeBaseModel)
            {
                foreach (BuildAxisFamily family in AllFamilies)
                {
                    Add(seen, row.FamilyModelBasePrefab(family));
                }
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
