using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ThinhnvTools
{
    public static class CopySelectedAssetPaths
    {
        [MenuItem("Tools/Thinhnv/Copy Selected Asset Paths %#c")]
        private static void CopyPathsToClipboard()
        {
            var selection = Selection.objects;
            if (selection == null || selection.Length == 0)
            {
                EditorUtility.DisplayDialog("Copy Selected Asset Paths", "No objects selected.", "OK");
                return;
            }

            var paths = new List<string>(selection.Length);
            foreach (var obj in selection)
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path) && obj is GameObject go)
                {
                    path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                }

                if (string.IsNullOrEmpty(path))
                {
                    path = $"<no asset path>: {obj.name}";
                }

                paths.Add(path.Replace("\\", "/"));
            }

            var result = string.Join("\n", paths);
            EditorGUIUtility.systemCopyBuffer = result;
            Debug.Log($"Copied {paths.Count} selected path(s) to clipboard.\n{result}");
        }

        [MenuItem("Tools/Thinhnv/Copy Selected Asset Paths %#c", true)]
        private static bool CopyPathsValidate()
        {
            return Selection.objects != null && Selection.objects.Length > 0;
        }
    }
}
