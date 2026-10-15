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

        /// <summary>
        /// Every file a reference could be hiding in. "Other" used to mean ScriptableObject, Material and
        /// AnimationClip only, which silently missed every other native type — an AnimatorController, a
        /// Preset, a Timeline or an AudioMixer pointing at the asset would be left dangling while the tool
        /// still reported success. It now takes all assets and subtracts the imported source files instead,
        /// which is complete by construction: anything Unity did not import from a foreign file holds its
        /// references in a serialized file we can rewrite.
        /// </summary>
        private static List<string> CollectCandidatePaths(bool includeScenes, bool includePrefabs, bool includeOther)
        {
            var list = new List<string>();
            var seen = new HashSet<string>();

            if (includeScenes)
            {
                AddRange(list, seen, FindAssetPaths("t:Scene"));
            }

            if (includePrefabs)
            {
                AddRange(list, seen, FindAssetPaths("t:Prefab"));
            }

            if (includeOther)
            {
                AddRange(list, seen, FindNativeAssetPaths());
            }

            return list;
        }

        private static void AddRange(List<string> list, HashSet<string> seen, IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                // Filters overlap — a prefab is also matched by the sweep below — and scanning a file twice
                // just costs time.
                if (seen.Add(path))
                {
                    list.Add(path);
                }
            }
        }

        /// <summary>Every asset except folders and imported source files (textures, models, audio, scripts).</summary>
        private static IEnumerable<string> FindNativeAssetPaths()
        {
            foreach (string path in FindAssetPaths("t:Object"))
            {
                if (AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                // An imported source file has no serialized object references of its own to rewrite.
                if (ImportedSourceExtensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                yield return path;
            }
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
