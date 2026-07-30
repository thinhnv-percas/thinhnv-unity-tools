using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// Authoring window for the game's object catalog: drag a model, a fractured model and a texture
    /// per size, press Build, and get the <c>ObjectElement</c> prefabs, the <c>BreakPieceEffect</c>
    /// shatter prefabs, the per-size materials and a filled-in <c>ObjectDefinitionSO</c>.
    ///
    /// Everything dragged in is kept in an <see cref="ObjectDefBuilderCacheSO"/> asset, so a definition
    /// can be rebuilt later without re-collecting its sources.
    ///
    /// Open via: Tools &gt; Thinhnv &gt; Object Definition Builder.
    /// </summary>
    public partial class ObjectDefBuilderWindow : EditorWindow
    {
        private const string CacheGuidPref = "Thinhnv.ObjectDefBuilder.CacheGuid";

        private ObjectDefBuilderCacheSO cache;
        private int entryIndex;
        private Vector2 scroll;
        private bool showSettings = true;
        private string status = string.Empty;

        [MenuItem("Tools/Thinhnv/Object Definition Builder")]
        public static void Open()
        {
            GetWindow<ObjectDefBuilderWindow>("Object Def Builder").minSize = new Vector2(560, 460);
        }

        private void OnEnable()
        {
            SmashMarketBridge.VerifySizeAxisMirror();
            RestoreCache();
        }

        private void OnGUI()
        {
            if (!SmashMarketBridge.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Game types not found: " + SmashMarketBridge.MissingTypesReport() +
                    "\nThis tool authors ObjectDefinitionSO / ObjectElement / BreakPieceEffect and needs " +
                    "them compiled. Fix any compile errors, or update the field-name constants in " +
                    "SmashMarketBridge if the game renamed them.",
                    MessageType.Error);
                return;
            }

            DrawCacheField();
            if (cache == null)
            {
                return;
            }

            DrawEntryToolbar();
            ObjectDefBuildEntry entry = CurrentEntry();
            if (entry == null)
            {
                EditorGUILayout.HelpBox("Add an object entry to start.", MessageType.Info);
                return;
            }

            entry.SyncRows();

            EditorGUI.BeginChangeCheck();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawEntrySettings(entry);
            DrawRows(entry);
            EditorGUILayout.EndScrollView();
            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(cache);
            }

            DrawBuildBar(entry);
        }

        private void DrawCacheField()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var picked = (ObjectDefBuilderCacheSO)EditorGUILayout.ObjectField(
                    "Build Cache", cache, typeof(ObjectDefBuilderCacheSO), false);
                if (picked != cache)
                {
                    cache = picked;
                    entryIndex = 0;
                    RememberCache();
                }

                if (cache == null && GUILayout.Button("Create", GUILayout.Width(70)))
                {
                    CreateCache();
                }
            }

            if (cache == null)
            {
                EditorGUILayout.HelpBox(
                    "Pick or create an Object Definition Build Cache. It stores every dragged model, " +
                    "fractured model and texture plus the assets built from them.",
                    MessageType.Info);
            }
        }

        private void DrawEntryToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                string[] labels = EntryLabels();
                entryIndex = Mathf.Clamp(entryIndex, 0, Mathf.Max(0, labels.Length - 1));
                if (labels.Length > 0)
                {
                    entryIndex = EditorGUILayout.Popup(entryIndex, labels, EditorStyles.toolbarPopup);
                }
                else
                {
                    EditorGUILayout.LabelField("No entries", EditorStyles.miniLabel);
                }

                if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(44)))
                {
                    AddEntry();
                }

                using (new EditorGUI.DisabledScope(labels.Length == 0))
                {
                    if (GUILayout.Button("Duplicate", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    {
                        DuplicateEntry();
                    }

                    if (GUILayout.Button("Delete", EditorStyles.toolbarButton, GUILayout.Width(56)))
                    {
                        DeleteEntry();
                    }
                }
            }
        }

        private string[] EntryLabels()
        {
            var labels = new string[cache.Entries.Count];
            for (int i = 0; i < labels.Length; i++)
            {
                ObjectDefBuildEntry entry = cache.Entries[i];
                labels[i] = string.IsNullOrWhiteSpace(entry.label) ? $"Entry {i}" : entry.label;
            }

            return labels;
        }

        private ObjectDefBuildEntry CurrentEntry() =>
            entryIndex >= 0 && entryIndex < cache.Entries.Count ? cache.Entries[entryIndex] : null;
    }
}
