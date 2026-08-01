using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Filling the cache backwards from an existing definition, so the tool can adopt prefabs it did not
    /// create. Only the prefab slots are recovered - the source models and textures are not recorded
    /// anywhere in the definition, so those stay as they were.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        /// <summary>
        /// Copy the definition's model and shatter prefabs into the entry's rows, adding rows for any
        /// magnitude it mentions. Base prefabs are recovered from the prefab-variant relationship, which is
        /// what the Bake buttons need. Slots the definition leaves empty keep whatever the cache held.
        /// </summary>
        private void FillFromDefinition(ObjectDefBuildEntry entry)
        {
            DefinitionRead read = ObjectDefinitionReader.Read(entry.definition, entry.breakSlot);
            if (read == null)
            {
                status = "Could not read that definition - see the Console.";
                return;
            }

            if (!string.IsNullOrEmpty(read.typeName))
            {
                entry.objectTypeName = read.typeName;
            }

            entry.maxMagnitude = Mathf.Max(entry.maxMagnitude, read.MaxMagnitude);
            entry.SyncRows();

            int filled = ApplyToRow(entry.rows[0], BuildAxis.None, read.baseModel, read.baseBreakPieces);

            foreach (DefinitionVariantRead variant in read.variants)
            {
                if (variant.magnitude < 1 || variant.magnitude > entry.rows.Count)
                {
                    continue;
                }

                filled += ApplyToRow(entry.rows[variant.magnitude - 1], variant.axis,
                    variant.model, variant.breakPieces);
            }

            EditorUtility.SetDirty(cache);
            status = filled == 0
                ? $"'{entry.definition.name}' has no prefabs to copy into the cache."
                : $"Filled {filled} prefab slot(s) from '{entry.definition.name}'. " +
                  "Source models and textures are not stored in a definition, so those were left alone.";
            Debug.Log($"[ObjectDefBuilder] {status}", entry.definition);
        }

        /// <summary>Write one axis of a row, recovering its base prefab and material along the way.</summary>
        private static int ApplyToRow(ObjectDefBuildRow row, BuildAxis axis,
            GameObject model, GameObject breakPieces)
        {
            int filled = 0;

            if (model != null)
            {
                row.SetModelPrefab(axis, model);
                row.modelBasePrefab = BaseOf(model) ?? row.modelBasePrefab;
                row.modelMaterial = MaterialOf(model) ?? row.modelMaterial;
                filled++;
            }

            if (breakPieces != null)
            {
                row.SetBreakPiece(axis, breakPieces);
                row.breakBasePrefab = BaseOf(breakPieces) ?? row.breakBasePrefab;
                row.pieceMaterial = MaterialOf(breakPieces) ?? row.pieceMaterial;
                filled++;
            }

            return filled;
        }

        /// <summary>The prefab a variant derives from, or null when it is a plain prefab.</summary>
        private static GameObject BaseOf(GameObject prefab) =>
            PrefabUtility.GetCorrespondingObjectFromSource(prefab) as GameObject;

        /// <summary>
        /// A prefab's first renderer material, so the row's cached material view matches its prefabs
        /// instead of showing empty next to filled slots.
        /// </summary>
        private static Material MaterialOf(GameObject prefab)
        {
            MeshRenderer renderer = prefab.GetComponentInChildren<MeshRenderer>(true);
            return renderer == null ? null : renderer.sharedMaterial;
        }
    }
}
