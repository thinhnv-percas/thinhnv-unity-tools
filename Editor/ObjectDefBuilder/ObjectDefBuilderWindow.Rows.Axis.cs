using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// One line of a size row's axis table: the axis, the size vector it resolves to, the prefab names it
    /// writes, its per-axis model slot, and the assets the last build produced for it.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        private void DrawAxisLine(ObjectDefBuildEntry entry, ObjectDefBuildRow row, BuildAxis axis)
        {
            string axisName = axis == BuildAxis.None ? "-" : axis.ToString();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(axisName, EditorStyles.miniBoldLabel, GUILayout.Width(36));
                EditorGUILayout.LabelField(
                    axis.SizeLabel(row.magnitude), EditorStyles.miniLabel, GUILayout.Width(90));
                EditorGUILayout.LabelField(
                    $"{ObjectDefNaming.ModelPrefab(entry, row.magnitude, axis)}   |   " +
                    ObjectDefNaming.BreakPiecePrefab(entry, row.magnitude, axis),
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    AxisSourceHint(entry, row, axis), EditorStyles.miniLabel, GUILayout.Width(78));
            }

            DrawAxisModelSlot(entry, row, axis);

            if (showResults)
            {
                DrawAxisResults(row, axis);
            }
        }

        private static string AxisSourceHint(ObjectDefBuildEntry entry, ObjectDefBuildRow row, BuildAxis axis)
        {
            if (axis == BuildAxis.None || entry.modelVariantMode == ModelVariantMode.Shared)
            {
                return string.Empty;
            }

            if (row.PerAxisModelSource(axis) != null)
            {
                return "own model";
            }

            return entry.modelVariantMode == ModelVariantMode.RotateBase ? "rotated" : "family base";
        }

        private static void DrawAxisModelSlot(ObjectDefBuildEntry entry, ObjectDefBuildRow row, BuildAxis axis)
        {
            if (axis == BuildAxis.None || entry.modelVariantMode == ModelVariantMode.Shared)
            {
                return;
            }

            string tooltip = entry.modelVariantMode == ModelVariantMode.RotateBase
                ? $"Drop a model built for {axis} to use it instead of rotating the base. Empty = rotated variant."
                : "Empty = use the family's base model above.";

            using (new EditorGUI.IndentLevelScope())
            {
                var current = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent($"Model {axis}", tooltip),
                    row.PerAxisModelSource(axis), typeof(GameObject), false);
                row.SetPerAxisModelSource(axis, current);
            }
        }

        /// <summary>Read-only view of the assets the last build wrote for one axis of a size.</summary>
        private static void DrawAxisResults(ObjectDefBuildRow row, BuildAxis axis)
        {
            using (new EditorGUI.DisabledScope(true))
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.ObjectField("Model", row.ModelPrefabFor(axis), typeof(GameObject), false);
                EditorGUILayout.ObjectField("Break Piece", row.BreakPieceFor(axis), typeof(GameObject), false);

                bool isFirstOfFamily = axis == BuildAxis.None || IsFirstOfFamily(axis);
                if (!isFirstOfFamily)
                {
                    return;
                }

                EditorGUILayout.ObjectField("Model Mat", row.modelMaterial, typeof(Material), false);
                EditorGUILayout.ObjectField("Piece Mat", row.pieceMaterial, typeof(Material), false);

                if (axis != BuildAxis.None)
                {
                    BuildAxisFamily family = BuildAxisExtensions.Family(axis);
                    EditorGUILayout.ObjectField("Model Base",
                        row.FamilyModelBasePrefab(family), typeof(GameObject), false);
                    EditorGUILayout.ObjectField("Break Base",
                        row.FamilyBreakBasePrefab(family), typeof(GameObject), false);
                }
            }
        }

        private static bool IsFirstOfFamily(BuildAxis axis) => axis switch
        {
            BuildAxis.X => true,
            BuildAxis.XY => true,
            BuildAxis.XYZ => true,
            _ => false,
        };
    }
}
