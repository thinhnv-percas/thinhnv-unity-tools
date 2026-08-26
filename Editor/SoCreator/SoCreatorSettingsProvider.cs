using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.SoCreator
{
    /// <summary>
    /// Project Settings page for SO Creator (Project Settings &gt; Percas Tools &gt; SO Creator).
    /// Lets the team scan the project for ScriptableObject types and pick which ones show up in
    /// the Assets &gt; Create &gt; SO Creator menu, with what path/file name — no attributes required
    /// on the ScriptableObject classes themselves.
    /// </summary>
    public static class SoCreatorSettingsProvider
    {
        public const string SettingsPath = "Project/Percas Tools/SO Creator";

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var state = new DrawState();
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "SO Creator",
                keywords = new[] { "scriptable object", "create asset menu", "quick create", "so creator" },
                guiHandler = _ => state.OnGui(),
            };
        }

        public static void ScanAndReport(SoCreatorSettings settings)
        {
            int added = Scan(settings);
            settings.SaveSettings();
            EditorUtility.DisplayDialog("SO Creator",
                $"Scan complete.\n\n{added} new ScriptableObject type(s) added " +
                $"({settings.Entries.Count} configured in total).",
                "OK");
        }

        private static int Scan(SoCreatorSettings settings)
        {
            var existing = new HashSet<string>();
            foreach (SoCreatorEntry entry in settings.Entries)
            {
                existing.Add(entry.AssemblyQualifiedTypeName);
            }

            int added = 0;
            foreach (Type type in SoCreatorTypeScanner.ScanProjectScriptableObjectTypes())
            {
                string key = type.AssemblyQualifiedName;
                if (existing.Contains(key))
                {
                    continue;
                }

                bool hasNativeMenu = Attribute.IsDefined(type, typeof(CreateAssetMenuAttribute));
                string niceName = ObjectNames.NicifyVariableName(type.Name);
                settings.Entries.Add(new SoCreatorEntry
                {
                    AssemblyQualifiedTypeName = key,
                    Enabled = !hasNativeMenu,
                    MenuPath = niceName,
                    FileName = "New " + niceName,
                });
                added++;
            }

            settings.Entries.Sort((a, b) => string.Compare(a.MenuPath, b.MenuPath, StringComparison.OrdinalIgnoreCase));
            return added;
        }

        private class DrawState
        {
            private Vector2 scroll;
            private string search = "";

            public void OnGui()
            {
                SoCreatorSettings settings = SoCreatorSettings.instance;

                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(
                    "Configure which ScriptableObject types can be created from Assets > Create > SO Creator, " +
                    "without adding a [CreateAssetMenu] attribute to each class. Scan the project after adding " +
                    "new ScriptableObject scripts to pick them up.",
                    MessageType.Info);

                EditorGUILayout.Space();
                DrawToolbar(settings);

                EditorGUILayout.Space();
                DrawList(settings);
            }

            private void DrawToolbar(SoCreatorSettings settings)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Scan Project", GUILayout.Width(120)))
                    {
                        ScanAndReport(settings);
                    }

                    GUILayout.FlexibleSpace();
                    GUILayout.Label("Search", GUILayout.Width(46));
                    search = EditorGUILayout.TextField(search, GUILayout.Width(200));
                }
            }

            private void DrawList(SoCreatorSettings settings)
            {
                List<SoCreatorEntry> entries = settings.Entries;
                if (entries.Count == 0)
                {
                    EditorGUILayout.HelpBox("No ScriptableObject types configured yet. Click \"Scan Project\" to find some.", MessageType.None);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    GUILayout.Label("On", GUILayout.Width(24));
                    GUILayout.Label("Type", GUILayout.Width(220));
                    GUILayout.Label("Menu Path", GUILayout.Width(200));
                    GUILayout.Label("File Name", GUILayout.ExpandWidth(true));
                    GUILayout.Space(24);
                }

                scroll = EditorGUILayout.BeginScrollView(scroll);

                SoCreatorEntry toRemove = null;
                EditorGUI.BeginChangeCheck();

                foreach (SoCreatorEntry entry in entries)
                {
                    if (!MatchesSearch(entry, search))
                    {
                        continue;
                    }

                    bool missing = entry.ResolveType() == null;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        entry.Enabled = EditorGUILayout.Toggle(entry.Enabled, GUILayout.Width(24));

                        using (new EditorGUI.DisabledScope(missing))
                        {
                            EditorGUILayout.LabelField(entry.DisplayTypeName, GUILayout.Width(220));
                        }

                        entry.MenuPath = EditorGUILayout.TextField(entry.MenuPath, GUILayout.Width(200));
                        entry.FileName = EditorGUILayout.TextField(entry.FileName, GUILayout.ExpandWidth(true));

                        if (GUILayout.Button("x", GUILayout.Width(24)))
                        {
                            toRemove = entry;
                        }
                    }

                    if (missing)
                    {
                        EditorGUILayout.HelpBox(
                            $"Script for '{entry.AssemblyQualifiedTypeName}' not found — it may have been renamed or removed.",
                            MessageType.Warning);
                    }
                }

                bool changed = EditorGUI.EndChangeCheck();
                EditorGUILayout.EndScrollView();

                if (toRemove != null)
                {
                    entries.Remove(toRemove);
                    changed = true;
                }

                if (changed)
                {
                    settings.SaveSettings();
                }
            }

            private static bool MatchesSearch(SoCreatorEntry entry, string search)
            {
                if (string.IsNullOrEmpty(search))
                {
                    return true;
                }

                return entry.DisplayTypeName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                       || (entry.MenuPath ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }
}
