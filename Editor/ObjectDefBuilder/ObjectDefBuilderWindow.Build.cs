using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Build orchestration: materials, then the object prefab and the shatter prefabs per size, then
    /// the merge into the definition. Deliberately does not wrap the run in
    /// <c>AssetDatabase.StartAssetEditing</c> - the axis variants have to load the base prefab that was
    /// just written, which a paused importer would not have available yet.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        private void DrawBuildBar(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(4);
            if (!string.IsNullOrEmpty(status))
            {
                EditorGUILayout.HelpBox(status, MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(entry.modelPrefix)))
            {
                if (GUILayout.Button("Build All Sizes", GUILayout.Height(32)))
                {
                    RequestBuild(entry);
                }
            }

            if (string.IsNullOrWhiteSpace(entry.modelPrefix))
            {
                EditorGUILayout.HelpBox("Set a Model Prefix - it names every generated asset.", MessageType.Warning);
            }
        }

        /// <summary>
        /// Queue a build for after this OnGUI pass. Creating prefabs and refreshing the AssetDatabase
        /// from inside a layout group can leave IMGUI mid-group when the importer repaints.
        /// </summary>
        private void RequestBuild(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            EditorApplication.delayCall += () =>
            {
                BuildEntry(entry, onlyMagnitude);
                Repaint();
            };
        }

        /// <summary>Build every included size, or just <paramref name="onlyMagnitude"/> when non-zero.</summary>
        private void BuildEntry(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            if (string.IsNullOrWhiteSpace(entry.modelPrefix))
            {
                status = "Set a Model Prefix first.";
                return;
            }

            if (entry.definition == null)
            {
                entry.definition = ObjectDefinitionWriter.LoadOrCreate(entry.definitionFolder,
                    string.IsNullOrEmpty(entry.objectTypeName) ? entry.modelPrefix : entry.objectTypeName);
            }

            var write = new DefinitionWrite();
            int sizes = 0;

            foreach (ObjectDefBuildRow row in entry.rows)
            {
                if (!row.include || !row.HasAnySource)
                {
                    continue;
                }

                if (onlyMagnitude > 0 && row.magnitude != onlyMagnitude)
                {
                    continue;
                }

                BuildRow(entry, row, write);
                sizes++;
            }

            ObjectDefinitionWriter.Write(entry.definition, SmashMarketBridge.ObjectTypeValue(entry.objectTypeName),
                write, entry.breakSlot);

            EditorUtility.SetDirty(cache);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            status = sizes == 0
                ? "Nothing to build - no included size has a Model or Break Model."
                : $"Built {sizes} size(s) into '{entry.definition.name}'. See the Console for warnings.";
            Debug.Log($"[ObjectDefBuilder] {status}", entry.definition);
        }

        /// <summary>
        /// Build one size: materials, the shatter prefabs, the object prefabs, then stage the definition
        /// rows. A prefab that came out null keeps whatever the cache already held, so a partially
        /// authored row never clears working data.
        /// </summary>
        private static void BuildRow(ObjectDefBuildEntry entry, ObjectDefBuildRow row, DefinitionWrite write)
        {
            BuildMaterials(entry, row);

            BreakBuildResult pieces = BreakPiecePrefabFactory.Build(
                entry, row.breakSource, row.pieceMaterial, row.magnitude);
            ModelBuildResult models = ObjectModelPrefabFactory.BuildForSize(
                entry, row, row.modelMaterial, row.magnitude);

            row.breakBasePrefab = pieces.basePrefab ?? row.breakBasePrefab;
            row.modelBasePrefab = models.basePrefab ?? row.modelBasePrefab;

            if (row.magnitude <= 1)
            {
                row.SetModelPrefab(BuildAxis.None, models.uniform ?? row.ModelPrefabFor(BuildAxis.None));
                row.SetBreakPiece(BuildAxis.None, pieces.uniform ?? row.BreakPieceFor(BuildAxis.None));
                write.baseModel = row.ModelPrefabFor(BuildAxis.None);
                write.baseBreakPieces = row.BreakPieceFor(BuildAxis.None);
                return;
            }

            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                row.SetModelPrefab(axis, models.ForAxis(axis) ?? row.ModelPrefabFor(axis));
                row.SetBreakPiece(axis, pieces.ForAxis(axis) ?? row.BreakPieceFor(axis));

                GameObject model = row.ModelPrefabFor(axis);
                GameObject breakPieces = row.BreakPieceFor(axis);
                if (model == null && breakPieces == null)
                {
                    continue;
                }

                write.variants.Add(new VariantWrite
                {
                    axis = axis, magnitude = row.magnitude, model = model, breakPieces = breakPieces,
                });
            }
        }

        private static void BuildMaterials(ObjectDefBuildEntry entry, ObjectDefBuildRow row)
        {
            row.modelMaterial = ObjectDefMaterialFactory.CreateOrUpdate(entry.materialFolder,
                ObjectDefNaming.ModelMaterial(entry, row.magnitude), entry.modelMaterial, row.modelTexture);
            row.pieceMaterial = ObjectDefMaterialFactory.CreateOrUpdate(entry.materialFolder,
                ObjectDefNaming.PieceMaterial(entry, row.magnitude), entry.pieceMaterial,
                row.pieceTexture != null ? row.pieceTexture : row.modelTexture);
        }
    }
}
