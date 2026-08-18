using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AssetChildConverter
{
    public partial class AssetChildConverterWindow
    {
        private enum AssetKind
        {
            Scene,
            Prefab,
            Other,
        }

        private class ReferenceHit
        {
            public string AssetPath;
            public AssetKind Kind;
            public int Count;
        }

        /// <summary>
        /// Walks every Scene/Prefab/native-asset file that depends on one of <paramref name="remap"/>'s
        /// keys and swaps each matching object reference for its mapped value. Must run before the
        /// original keys are removed from the project, otherwise dependency lookup and reference
        /// comparison can no longer see them.
        /// </summary>
        private List<ReferenceHit> RepointReferences(
            Dictionary<Object, Object> remap, bool includeScenes, bool includePrefabs, bool includeOther)
        {
            var hits = new List<ReferenceHit>();
            if (remap.Count == 0)
            {
                return hits;
            }

            HashSet<string> originalPaths = GetAssetPaths(remap.Keys);
            List<string> candidates = CollectCandidatePaths(includeScenes, includePrefabs, includeOther);

            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    string path = candidates[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Asset Child Converter",
                            $"Scanning {path}",
                            (float)i / candidates.Count))
                    {
                        break;
                    }

                    if (!DependsOnAny(path, originalPaths))
                    {
                        continue;
                    }

                    string extension = Path.GetExtension(path);
                    int count;
                    AssetKind kind;

                    if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = AssetKind.Scene;
                        count = ProcessScene(path, remap);
                    }
                    else if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = AssetKind.Prefab;
                        count = ProcessPrefab(path, remap);
                    }
                    else
                    {
                        kind = AssetKind.Other;
                        count = ProcessAssetFile(path, remap);
                    }

                    if (count > 0)
                    {
                        hits.Add(new ReferenceHit { AssetPath = path, Kind = kind, Count = count });
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            return hits;
        }

        private static List<string> CollectCandidatePaths(bool includeScenes, bool includePrefabs, bool includeOther)
        {
            var list = new List<string>();
            if (includeScenes)
            {
                list.AddRange(FindAssetPaths("t:Scene"));
            }

            if (includePrefabs)
            {
                list.AddRange(FindAssetPaths("t:Prefab"));
            }

            if (includeOther)
            {
                list.AddRange(FindAssetPaths("t:ScriptableObject"));
                list.AddRange(FindAssetPaths("t:Material"));
                list.AddRange(FindAssetPaths("t:AnimationClip"));
            }

            return list;
        }

        private static IEnumerable<string> FindAssetPaths(string filter)
        {
            foreach (string guid in AssetDatabase.FindAssets(filter))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }

        private static HashSet<string> GetAssetPaths(IEnumerable<Object> objects)
        {
            var set = new HashSet<string>();
            foreach (Object obj in objects)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                if (!string.IsNullOrEmpty(path))
                {
                    set.Add(path);
                }
            }

            return set;
        }

        private static bool DependsOnAny(string assetPath, HashSet<string> originalPaths)
        {
            foreach (string dependency in AssetDatabase.GetDependencies(assetPath, true))
            {
                if (originalPaths.Contains(dependency))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Opens the scene additively if it isn't already loaded, processes it, saves + closes it again.</summary>
        private static int ProcessScene(string path, Dictionary<Object, Object> remap)
        {
            bool wasAlreadyOpen = TryGetOpenScene(path, out Scene scene);
            if (!wasAlreadyOpen)
            {
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            }

            int count = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null)
                    {
                        continue;
                    }

                    count += ReplaceInObject(component, remap);
                }
            }

            if (count > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            if (!wasAlreadyOpen)
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            return count;
        }

        private static bool TryGetOpenScene(string path, out Scene scene)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene s = SceneManager.GetSceneAt(i);
                if (s.path == path)
                {
                    scene = s;
                    return true;
                }
            }

            scene = default;
            return false;
        }

        private static int ProcessPrefab(string path, Dictionary<Object, Object> remap)
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
            {
                return 0;
            }

            int count = 0;
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }

                count += ReplaceInObject(component, remap);
            }

            if (count > 0)
            {
                EditorUtility.SetDirty(root);
                PrefabUtility.SavePrefabAsset(root);
            }

            return count;
        }

        private static int ProcessAssetFile(string path, Dictionary<Object, Object> remap)
        {
            int count = 0;
            foreach (Object obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (obj == null)
                {
                    continue;
                }

                int c = ReplaceInObject(obj, remap);
                if (c > 0)
                {
                    count += c;
                    EditorUtility.SetDirty(obj);
                }
            }

            return count;
        }

        /// <summary>Walks every ObjectReference property of <paramref name="target"/> and swaps any value matching a remap key.</summary>
        private static int ReplaceInObject(Object target, Dictionary<Object, Object> remap)
        {
            var so = new SerializedObject(target);
            SerializedProperty prop = so.GetIterator();
            bool enterChildren = true;
            int count = 0;

            while (prop.Next(enterChildren))
            {
                enterChildren = true;

                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                enterChildren = false;

                if (prop.objectReferenceValue != null && remap.TryGetValue(prop.objectReferenceValue, out Object replacement))
                {
                    count++;
                    prop.objectReferenceValue = replacement;
                }
            }

            if (count > 0)
            {
                so.ApplyModifiedProperties();
            }

            return count;
        }
    }
}
