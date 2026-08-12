using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Thinhnv.UnityTools.MaterialReferenceReplacer
{
    public partial class MaterialReferenceReplacerWindow
    {
        private enum AssetKind
        {
            Scene,
            Prefab,
            ScriptableObject,
        }

        private class ReferenceHit
        {
            public string AssetPath;
            public AssetKind Kind;
            public int Count;
        }

        /// <summary>
        /// Scans (apply == false) or scans-and-writes (apply == true) every Scene/Prefab/ScriptableObject
        /// asset that depends on one of the old materials, swapping matching references for <see cref="newMaterial"/>.
        /// </summary>
        private List<ReferenceHit> Execute(bool apply)
        {
            var hits = new List<ReferenceHit>();

            HashSet<Material> targets = BuildTargetSet();
            if (targets.Count == 0 || newMaterial == null)
            {
                return hits;
            }

            HashSet<string> oldMaterialPaths = GetAssetPaths(targets);
            List<string> candidates = CollectCandidatePaths();

            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    string path = candidates[i];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Material Reference Replacer",
                            $"Scanning {path}",
                            (float)i / candidates.Count))
                    {
                        break;
                    }

                    if (!DependsOnAny(path, oldMaterialPaths))
                    {
                        continue;
                    }

                    string extension = Path.GetExtension(path);
                    int count;
                    AssetKind kind;

                    if (extension.Equals(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = AssetKind.Scene;
                        count = ProcessScene(path, targets, apply);
                    }
                    else if (extension.Equals(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        kind = AssetKind.Prefab;
                        count = ProcessPrefab(path, targets, apply);
                    }
                    else
                    {
                        kind = AssetKind.ScriptableObject;
                        count = ProcessScriptableObject(path, targets, apply);
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

            if (apply)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            return hits;
        }

        private List<string> CollectCandidatePaths()
        {
            var list = new List<string>();
            if (scanScenes)
            {
                list.AddRange(FindAssetPaths("t:Scene"));
            }

            if (scanPrefabs)
            {
                list.AddRange(FindAssetPaths("t:Prefab"));
            }

            if (scanScriptableObjects)
            {
                list.AddRange(FindAssetPaths("t:ScriptableObject"));
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

        private static HashSet<string> GetAssetPaths(IEnumerable<Material> materials)
        {
            var set = new HashSet<string>();
            foreach (Material m in materials)
            {
                string path = AssetDatabase.GetAssetPath(m);
                if (!string.IsNullOrEmpty(path))
                {
                    set.Add(path);
                }
            }

            return set;
        }

        private static bool DependsOnAny(string assetPath, HashSet<string> materialPaths)
        {
            foreach (string dependency in AssetDatabase.GetDependencies(assetPath, true))
            {
                if (materialPaths.Contains(dependency))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Opens the scene additively if it isn't already loaded, processes it, saves + closes it again.</summary>
        private int ProcessScene(string path, HashSet<Material> targets, bool apply)
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

                    count += CountOrReplaceInObject(component, targets, apply);
                }
            }

            if (apply && count > 0)
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

        private int ProcessPrefab(string path, HashSet<Material> targets, bool apply)
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

                count += CountOrReplaceInObject(component, targets, apply);
            }

            if (apply && count > 0)
            {
                EditorUtility.SetDirty(root);
                PrefabUtility.SavePrefabAsset(root);
            }

            return count;
        }

        private int ProcessScriptableObject(string path, HashSet<Material> targets, bool apply)
        {
            int count = 0;
            foreach (UnityEngine.Object obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (obj is not ScriptableObject so)
                {
                    continue;
                }

                int c = CountOrReplaceInObject(so, targets, apply);
                if (c > 0)
                {
                    count += c;
                    if (apply)
                    {
                        EditorUtility.SetDirty(so);
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Walks every serialized property of <paramref name="target"/> and counts (apply == false) or
        /// swaps (apply == true) ObjectReference properties pointing at one of <paramref name="targetMaterials"/>.
        /// </summary>
        private int CountOrReplaceInObject(UnityEngine.Object target, HashSet<Material> targetMaterials, bool apply)
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

                if (prop.objectReferenceValue is Material mat && targetMaterials.Contains(mat))
                {
                    count++;
                    if (apply)
                    {
                        prop.objectReferenceValue = newMaterial;
                    }
                }
            }

            if (apply && count > 0)
            {
                so.ApplyModifiedProperties();
            }

            return count;
        }
    }
}
