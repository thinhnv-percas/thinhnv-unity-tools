using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The build bar: which targets a run writes, the presets for a narrowed pass, and the button itself.
    /// Unticking a target never clears it - the row keeps what the last build cached, so a shatter-only or
    /// materials-only run still leaves the definition consistent.
    /// </summary>
    public partial class ObjectDefBuilderWindow
    {
        /// <summary>Assets the most recent run wrote, for the result bar to act on.</summary>
        private readonly List<Object> lastBuilt = new List<Object>();

        private string lastBuiltFolder;

        private void DrawBuildBar(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.Space(4);
            if (!string.IsNullOrEmpty(status))
            {
                EditorGUILayout.HelpBox(status, MessageType.Info);
            }

            DrawBuildTargets(entry);

            bool nothingSelected = entry.buildTargets == BuildTargets.None;
            using (new EditorGUI.DisabledScope(
                string.IsNullOrWhiteSpace(entry.modelPrefix) || nothingSelected))
            {
                if (GUILayout.Button(BuildButtonLabel(entry), GUILayout.Height(32)))
                {
                    RequestBuild(entry);
                }
            }

            if (string.IsNullOrWhiteSpace(entry.modelPrefix))
            {
                EditorGUILayout.HelpBox("Set a Model Prefix - it names every generated asset.", MessageType.Warning);
            }
            else if (nothingSelected)
            {
                EditorGUILayout.HelpBox("Tick at least one Build target.", MessageType.Warning);
            }

            DrawBuildResult(entry);
        }

        /// <summary>
        /// What to do with the run that just finished: jump to the assets it wrote, or open the folder.
        /// Only lists what this run actually produced - a narrowed pass shows only its own output.
        /// </summary>
        private void DrawBuildResult(ObjectDefBuildEntry entry)
        {
            lastBuilt.RemoveAll(asset => asset == null);
            if (lastBuilt.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Last build: {lastBuilt.Count} asset(s)",
                    EditorStyles.miniLabel, GUILayout.Width(130));

                if (GUILayout.Button("Select All", EditorStyles.miniButtonLeft))
                {
                    Selection.objects = lastBuilt.ToArray();
                    EditorGUIUtility.PingObject(lastBuilt[0]);
                }

                using (new EditorGUI.DisabledScope(entry.definition == null))
                {
                    if (GUILayout.Button("Ping Definition", EditorStyles.miniButtonMid))
                    {
                        Selection.activeObject = entry.definition;
                        EditorGUIUtility.PingObject(entry.definition);
                    }
                }

                if (GUILayout.Button("Open Folder", EditorStyles.miniButtonMid))
                {
                    EditorUtility.RevealInFinder(lastBuiltFolder);
                }

                if (GUILayout.Button("Clear", EditorStyles.miniButtonRight, GUILayout.Width(46)))
                {
                    lastBuilt.Clear();
                    status = string.Empty;
                }
            }
        }

        /// <summary>
        /// What a build run writes. Unticking a target does not clear it - the row keeps what the last
        /// build cached, so a shatter-only or materials-only pass still leaves the definition consistent.
        /// </summary>
        private static void DrawBuildTargets(ObjectDefBuildEntry entry)
        {
            EditorGUILayout.LabelField("Build", EditorStyles.miniBoldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                Toggle(entry, BuildTargets.Materials, "Materials", 82);
                Toggle(entry, BuildTargets.BreakPieces, "Break Pieces", 96);
                Toggle(entry, BuildTargets.Models, "Models", 68);
                Toggle(entry, BuildTargets.Definition, "Definition", 82);
                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Break only", EditorStyles.miniButtonLeft, GUILayout.Width(74)))
                {
                    entry.buildTargets = BuildTargets.BreakPieces | BuildTargets.Definition;
                }

                if (GUILayout.Button("Mats only", EditorStyles.miniButtonMid, GUILayout.Width(70)))
                {
                    entry.buildTargets = BuildTargets.Materials;
                }

                if (GUILayout.Button("All", EditorStyles.miniButtonRight, GUILayout.Width(38)))
                {
                    entry.buildTargets = BuildTargets.All;
                }
            }
        }

        private static void Toggle(ObjectDefBuildEntry entry, BuildTargets target, string label, float width)
        {
            bool on = EditorGUILayout.ToggleLeft(label, entry.Builds(target), GUILayout.Width(width));
            if (on)
            {
                entry.buildTargets |= target;
            }
            else
            {
                entry.buildTargets &= ~target;
            }
        }

        private static string BuildButtonLabel(ObjectDefBuildEntry entry) =>
            entry.buildTargets == BuildTargets.All
                ? "Build All Sizes"
                : $"Build All Sizes ({entry.buildTargets})";
    }
}
