using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>The per-size table: source slots to drag into, and the assets each axis resolves to.</summary>
    public partial class ObjectDefBuilderWindow
    {
        private bool showResults;

        private void DrawRows(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Sizes", EditorStyles.boldLabel);
            DrawMagnitudeCount(entry);
            DrawAutoFillBar(entry);

            foreach (ObjectDefBuildRow row in entry.rows)
            {
                DrawRow(entry, row);
            }

            EditorGUILayout.Space(4);
            showResults = EditorGUILayout.ToggleLeft("Show built assets per axis", showResults);
        }

        private static void DrawMagnitudeCount(ObjectDefBuildEntry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                int requested = EditorGUILayout.IntField(
                    new GUIContent("Magnitude Count",
                        "Number of sizes: 5 for most objects, 8 for Ice Square. Rows follow this value."),
                    entry.maxMagnitude);
                entry.maxMagnitude = Mathf.Clamp(requested, 1, ObjectDefBuildEntry.MagnitudeLimit);

                EditorGUILayout.LabelField(
                    $"1 .. {entry.maxMagnitude}", EditorStyles.miniLabel, GUILayout.Width(60));
            }
        }

        private void DrawAutoFillBar(ObjectDefBuildEntry entry)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                entry.sourceFolder = EditorGUILayout.TextField("Source Folder", entry.sourceFolder);
                if (GUILayout.Button("Auto-Fill", GUILayout.Width(70)))
                {
                    status = ObjectDefSourceScanner.AutoFill(entry);
                    EditorUtility.SetDirty(cache);
                }
            }

            EditorGUILayout.LabelField(
                " ", "Matches '1x<n>' in file names and replaces the slots. Names containing 'break' " +
                     "fill the break slot; a texture fills both Model and Piece.",
                EditorStyles.miniLabel);
        }

        private void DrawRow(ObjectDefBuildEntry entry, ObjectDefBuildRow row)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    row.include = EditorGUILayout.ToggleLeft(
                        $"Magnitude {row.magnitude}", row.include, EditorStyles.boldLabel, GUILayout.Width(110));
                    GUILayout.FlexibleSpace();

                    using (new EditorGUI.DisabledScope(!row.HasAnySource))
                    {
                        if (GUILayout.Button("Build Size", GUILayout.Width(80)))
                        {
                            RequestBuild(entry, row.magnitude);
                        }
                    }

                    using (new EditorGUI.DisabledScope(!row.HasBakeTarget))
                    {
                        if (GUILayout.Button("Bake", GUILayout.Width(46)))
                        {
                            RequestBake(entry, row.magnitude);
                        }
                    }
                }

                using (new EditorGUI.DisabledScope(!row.include))
                {
                    DrawFamilySources(row);
                    DrawAxisTable(entry, row);
                }
            }
        }

        private static void DrawFamilySources(ObjectDefBuildRow row)
        {
            EditorGUILayout.LabelField("Bar (single-axis)", EditorStyles.miniBoldLabel);
            row.modelSource = (GameObject)EditorGUILayout.ObjectField(
                "Model", row.modelSource, typeof(GameObject), false);
            row.breakSource = (GameObject)EditorGUILayout.ObjectField(
                "Break Model", row.breakSource, typeof(GameObject), false);
            row.modelTexture = (Texture2D)EditorGUILayout.ObjectField(
                "Model Texture", row.modelTexture, typeof(Texture2D), false);
            row.pieceTexture = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Piece Texture", "Empty = reuse the model texture."),
                row.pieceTexture, typeof(Texture2D), false);

            EditorGUILayout.LabelField("Plate (dual-axis)", EditorStyles.miniBoldLabel);
            row.plateModelSource = (GameObject)EditorGUILayout.ObjectField(
                "Plate Model", row.plateModelSource, typeof(GameObject), false);
            row.plateBreakSource = (GameObject)EditorGUILayout.ObjectField(
                "Plate Break", row.plateBreakSource, typeof(GameObject), false);

            EditorGUILayout.LabelField("Cube (triple-axis)", EditorStyles.miniBoldLabel);
            row.cubeModelSource = (GameObject)EditorGUILayout.ObjectField(
                "Cube Model", row.cubeModelSource, typeof(GameObject), false);
            row.cubeBreakSource = (GameObject)EditorGUILayout.ObjectField(
                "Cube Break", row.cubeBreakSource, typeof(GameObject), false);
        }

        /// <summary>
        /// Per-axis breakdown grouped by family: Bar (X/Y/Z), Plate (XY/YZ/XZ), Cube (XYZ).
        /// Magnitude 1 has no axis — it is the uniform 1x1x1 base.
        /// </summary>
        private void DrawAxisTable(ObjectDefBuildEntry entry, ObjectDefBuildRow row)
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Axis -> size -> assets", EditorStyles.miniBoldLabel);

            using (new EditorGUI.IndentLevelScope())
            {
                if (row.magnitude <= 1)
                {
                    DrawAxisLine(entry, row, BuildAxis.None);
                    return;
                }

                DrawFamilyGroup(entry, row, "Bar", BuildAxisExtensions.BarAxes);
                DrawFamilyGroup(entry, row, "Plate", BuildAxisExtensions.PlateAxes);
                DrawFamilyGroup(entry, row, "Cube", BuildAxisExtensions.CubeAxes);
            }
        }

        private void DrawFamilyGroup(ObjectDefBuildEntry entry, ObjectDefBuildRow row,
            string label, BuildAxis[] axes)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            foreach (BuildAxis axis in axes)
            {
                DrawAxisLine(entry, row, axis);
            }
        }
    }
}
