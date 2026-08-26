using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.SoCreator
{
    /// <summary>
    /// Entry points for SO Creator: a "Create" menu built from the types enabled in
    /// Project Settings &gt; Thinhnv Tools &gt; SO Creator, plus shortcuts to that settings page.
    /// No <c>[CreateAssetMenu]</c> attribute is required on the ScriptableObject types themselves.
    /// </summary>
    public static class SoCreatorMenu
    {
        [MenuItem("Assets/Create/SO Creator", false, 0)]
        private static void OpenCreateMenu()
        {
            List<SoCreatorEntry> entries = SoCreatorSettings.instance.Entries.FindAll(e => e.Enabled && e.ResolveType() != null);
            if (entries.Count == 0)
            {
                if (EditorUtility.DisplayDialog("SO Creator",
                        "No ScriptableObject types are set up yet.\n\n" +
                        "Open Project Settings > Thinhnv Tools > SO Creator and scan the project to set some up.",
                        "Open Settings", "Cancel"))
                {
                    OpenSettings();
                }

                return;
            }

            entries.Sort((a, b) => string.Compare(EffectiveMenuPath(a), EffectiveMenuPath(b), System.StringComparison.OrdinalIgnoreCase));

            var menu = new GenericMenu();
            foreach (SoCreatorEntry entry in entries)
            {
                SoCreatorEntry captured = entry;
                menu.AddItem(new GUIContent(EffectiveMenuPath(captured)), false, () => SoCreatorAssetFactory.CreateAssetInteractive(captured));
            }

            // GenericMenu.ShowAsContext() reads Event.current, which is null when a MenuItem is
            // invoked from the main menu bar (as opposed to an OnGUI context click) — it then
            // throws and the menu never appears. DropDown takes an explicit screen-space rect
            // instead, so it works reliably from here.
            Rect mainWindow = EditorGUIUtility.GetMainWindowPosition();
            var dropDownRect = new Rect(mainWindow.x + mainWindow.width / 2f, mainWindow.y + mainWindow.height / 2f, 0, 0);
            menu.DropDown(dropDownRect);
        }

        [MenuItem("Tools/Thinhnv/SO Creator/Open Settings")]
        private static void OpenSettings()
        {
            SettingsService.OpenProjectSettings(SoCreatorSettingsProvider.SettingsPath);
        }

        [MenuItem("Tools/Thinhnv/SO Creator/Scan Project")]
        private static void ScanFromToolsMenu()
        {
            SoCreatorSettingsProvider.ScanAndReport(SoCreatorSettings.instance);
        }

        private static string EffectiveMenuPath(SoCreatorEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry.MenuPath) ? entry.DisplayTypeName : entry.MenuPath;
        }
    }
}
