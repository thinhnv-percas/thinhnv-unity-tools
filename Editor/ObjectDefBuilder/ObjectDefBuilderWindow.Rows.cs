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

        /// <summary>
        /// How many magnitudes this object has. Rows are added/removed to match on the next repaint;
        /// shrinking drops the trailing rows, so their cached sources are lost.
        /// </summary>
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
                        var bake = new GUIContent("Bake",
                            "Flatten this size's already-built prefabs into baked meshes. Works off the " +
                            "cached prefabs, so it does not need a rebuild. Variants are skipped - their " +
                            "base is baked instead.");
                        if (GUILayout.Button(bake, GUILayout.Width(46)))
                        {
                            RequestBake(entry, row.magnitude);
                        }
                    }
                }

                using (new EditorGUI.DisabledScope(!row.include))
                {
                    row.modelSource = (GameObject)EditorGUILayout.ObjectField(
                        "Model", row.modelSource, typeof(GameObject), false);
                    row.breakSource = (GameObject)EditorGUILayout.ObjectField(
                        "Break Model", row.breakSource, typeof(GameObject), false);
                    row.modelTexture = (Texture2D)EditorGUILayout.ObjectField(
                        "Model Texture", row.modelTexture, typeof(Texture2D), false);
                    row.pieceTexture = (Texture2D)EditorGUILayout.ObjectField(
                        new GUIContent("Piece Texture", "Empty = reuse the model texture."),
                        row.pieceTexture, typeof(Texture2D), false);

                    DrawAxisTable(entry, row);
                }
            }
        }

        /// <summary>
        /// What each stretched axis of this size resolves to: the level-data size vector and the two
        /// prefab names that get written for it. Magnitude 1 has no axis - it is the uniform 1x1x1 base.
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

                foreach (BuildAxis axis in BuildAxisExtensions.All)
                {
                    DrawAxisLine(entry, row, axis);
                }
            }
        }
    }
}
