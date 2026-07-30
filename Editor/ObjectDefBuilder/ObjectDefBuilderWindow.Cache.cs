using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>Locating, creating and editing the window's <see cref="ObjectDefBuilderCacheSO"/>.</summary>
    public partial class ObjectDefBuilderWindow
    {
        private const string DefaultCacheFolder = "Assets/@SmashMarket/Data/Object";
        private const string DefaultCacheName = "ObjectDefBuildCache";

        /// <summary>Re-select the cache used last, else the only one in the project.</summary>
        private void RestoreCache()
        {
            string guid = EditorPrefs.GetString(CacheGuidPref, string.Empty);
            if (!string.IsNullOrEmpty(guid))
            {
                cache = AssetDatabase.LoadAssetAtPath<ObjectDefBuilderCacheSO>(
                    AssetDatabase.GUIDToAssetPath(guid));
            }

            if (cache != null)
            {
                return;
            }

            string[] found = AssetDatabase.FindAssets($"t:{nameof(ObjectDefBuilderCacheSO)}");
            if (found.Length > 0)
            {
                cache = AssetDatabase.LoadAssetAtPath<ObjectDefBuilderCacheSO>(
                    AssetDatabase.GUIDToAssetPath(found[0]));
                RememberCache();
            }
        }

        private void RememberCache()
        {
            string path = cache == null ? string.Empty : AssetDatabase.GetAssetPath(cache);
            EditorPrefs.SetString(CacheGuidPref,
                string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path));
        }

        private void CreateCache()
        {
            string folder = AssetDatabase.IsValidFolder(DefaultCacheFolder) ? DefaultCacheFolder : "Assets";
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Object Definition Build Cache", DefaultCacheName, "asset",
                "Where should the build cache live?", folder);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var created = CreateInstance<ObjectDefBuilderCacheSO>();
            AssetDatabase.CreateAsset(created, path);
            AssetDatabase.SaveAssets();

            cache = created;
            entryIndex = 0;
            RememberCache();
        }

        private void AddEntry()
        {
            Undo.RecordObject(cache, "Add Object Entry");
            var entry = new ObjectDefBuildEntry();
            entry.SyncRows();
            cache.Entries.Add(entry);
            entryIndex = cache.Entries.Count - 1;
            EditorUtility.SetDirty(cache);
        }

        /// <summary>
        /// Copy the selected entry. Round-tripping through JsonUtility keeps asset references
        /// (serialized as instance ids) without hand-copying every field.
        /// </summary>
        private void DuplicateEntry()
        {
            ObjectDefBuildEntry source = CurrentEntry();
            if (source == null)
            {
                return;
            }

            Undo.RecordObject(cache, "Duplicate Object Entry");
            var copy = JsonUtility.FromJson<ObjectDefBuildEntry>(JsonUtility.ToJson(source));
            copy.label = $"{source.label} Copy";
            cache.Entries.Insert(entryIndex + 1, copy);
            entryIndex++;
            EditorUtility.SetDirty(cache);
        }

        private void DeleteEntry()
        {
            ObjectDefBuildEntry entry = CurrentEntry();
            if (entry == null)
            {
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "Delete Entry",
                $"Remove '{entry.label}' from the build cache?\n\n" +
                "Prefabs, materials and the definition asset it built are left on disk.",
                "Delete", "Cancel");
            if (!confirmed)
            {
                return;
            }

            Undo.RecordObject(cache, "Delete Object Entry");
            cache.Entries.RemoveAt(entryIndex);
            entryIndex = Mathf.Clamp(entryIndex - 1, 0, Mathf.Max(0, cache.Entries.Count - 1));
            EditorUtility.SetDirty(cache);
        }
    }
}
