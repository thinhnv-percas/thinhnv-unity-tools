using System.Collections.Generic;
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
        private void RequestBuild(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            EditorApplication.delayCall += () =>
            {
                BuildEntry(entry, onlyMagnitude);
                Repaint();
            };
        }

        private void BuildEntry(ObjectDefBuildEntry entry, int onlyMagnitude = 0)
        {
            if (string.IsNullOrWhiteSpace(entry.modelPrefix))
            {
                status = "Set a Model Prefix first.";
                return;
            }

            if (entry.buildTargets == BuildTargets.None)
            {
                status = "Nothing selected to build - tick at least one Build target.";
                return;
            }

            if (entry.Builds(BuildTargets.Definition) && entry.definition == null)
            {
                entry.definition = ObjectDefinitionWriter.LoadOrCreate(entry.definitionFolder,
                    string.IsNullOrEmpty(entry.objectTypeName) ? entry.modelPrefix : entry.objectTypeName);
            }

            var write = new DefinitionWrite();
            int sizes = 0;
            lastBuilt.Clear();
            lastBuiltFolder = entry.prefabFolder;

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

                BuildRow(entry, row, write, lastBuilt);
                sizes++;
            }

            if (entry.Builds(BuildTargets.Definition))
            {
                ObjectDefinitionWriter.Write(entry.definition,
                    SmashMarketBridge.ObjectTypeValue(entry.objectTypeName), write, entry.breakSlot);
                Track(lastBuilt, entry.definition);
            }

            EditorUtility.SetDirty(cache);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            ReportBuild(entry, sizes);
        }

        private void ReportBuild(ObjectDefBuildEntry entry, int sizes)
        {
            if (sizes == 0)
            {
                status = "Nothing to build - no included size has a source.";
                Debug.Log($"[ObjectDefBuilder] {status}");
                return;
            }

            string scope = entry.buildTargets == BuildTargets.All
                ? "everything"
                : entry.buildTargets.ToString();
            string target = entry.Builds(BuildTargets.Definition) && entry.definition != null
                ? $" into '{entry.definition.name}'"
                : string.Empty;

            status = $"Built {scope} for {sizes} size(s){target}. See the Console for warnings.";
            Debug.Log($"[ObjectDefBuilder] {status}", entry.definition);
        }

        private static void BuildRow(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            DefinitionWrite write, List<Object> built)
        {
            if (entry.Builds(BuildTargets.Materials))
            {
                BuildMaterials(entry, row);
                Track(built, row.modelMaterial);
                Track(built, row.pieceMaterial);
            }

            BreakBuildResult pieces = entry.Builds(BuildTargets.BreakPieces)
                ? BreakPiecePrefabFactory.Build(entry, row, row.pieceMaterial, row.magnitude)
                : default;

            ModelBuildResult models = entry.Builds(BuildTargets.Models)
                ? ObjectModelPrefabFactory.BuildForSize(entry, row, row.modelMaterial, row.magnitude)
                : default;

            TrackResults(built, pieces, models);
            CacheResults(row, pieces, models);
            StageDefinitionWrite(row, pieces, models, write);
        }

        private static void CacheResults(ObjectDefBuildRow row,
            BreakBuildResult pieces, ModelBuildResult models)
        {
            foreach (BuildAxisFamily family in AllFamilies)
            {
                row.SetFamilyModelBasePrefab(family,
                    models.BasePrefab(family) ?? row.FamilyModelBasePrefab(family));
                row.SetFamilyBreakBasePrefab(family,
                    pieces.BasePrefab(family) ?? row.FamilyBreakBasePrefab(family));
            }
        }

        private static void StageDefinitionWrite(ObjectDefBuildRow row,
            BreakBuildResult pieces, ModelBuildResult models, DefinitionWrite write)
        {
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

        private static readonly BuildAxisFamily[] AllFamilies =
            { BuildAxisFamily.Bar, BuildAxisFamily.Plate, BuildAxisFamily.Cube };

        private static void TrackResults(List<Object> built, BreakBuildResult pieces, ModelBuildResult models)
        {
            Track(built, pieces.uniform);
            Track(built, models.uniform);

            foreach (BuildAxisFamily family in AllFamilies)
            {
                Track(built, pieces.BasePrefab(family));
                Track(built, models.BasePrefab(family));
            }

            foreach (BuildAxis axis in BuildAxisExtensions.All)
            {
                Track(built, pieces.ForAxis(axis));
                Track(built, models.ForAxis(axis));
            }
        }

        private static void Track(List<Object> built, Object asset)
        {
            if (asset != null && !built.Contains(asset))
            {
                built.Add(asset);
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
