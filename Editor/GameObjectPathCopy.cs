using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ThinhnvTools
{
    /// <summary>
    /// GameObject › Copy GameObject Path (also in the Hierarchy's context menu): copies the selected
    /// objects' hierarchy paths ("Root/Child/Leaf"), one per line in Hierarchy order.
    /// </summary>
    public static class GameObjectPathCopy
    {
        private const string MenuPath = "GameObject/Copy GameObject Path";

        [MenuItem(MenuPath, false, 49)]
        private static void CopyPath(MenuCommand command)
        {
            List<GameObject> selected = SelectedInHierarchyOrder();
            if (selected.Count == 0)
            {
                return;
            }

            // From the Hierarchy's context menu Unity runs the item once per selected object
            // (command.context is that object): copy once, on the first one.
            if (command.context != null && command.context != selected[0])
            {
                return;
            }

            var paths = new StringBuilder();
            foreach (GameObject go in selected)
            {
                if (paths.Length > 0)
                {
                    paths.Append('\n');
                }

                paths.Append(GetGameObjectPath(go));
            }

            EditorGUIUtility.systemCopyBuffer = paths.ToString();
            Debug.Log(selected.Count == 1
                ? $"Copied GameObject Path: {paths}"
                : $"Copied {selected.Count} GameObject Paths:\n{paths}", selected[0]);
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateCopyPath()
        {
            return Selection.gameObjects.Length > 0;
        }

        /// <summary>The selected GameObjects sorted as the Hierarchy shows them (scene, then sibling order).</summary>
        private static List<GameObject> SelectedInHierarchyOrder()
        {
            return Selection.gameObjects
                .Distinct()
                .OrderBy(go => go, Comparer<GameObject>.Create(CompareHierarchyOrder))
                .ToList();
        }

        private static int CompareHierarchyOrder(GameObject a, GameObject b)
        {
            int scene = SceneOrder(a).CompareTo(SceneOrder(b));
            if (scene != 0)
            {
                return scene;
            }

            List<int> ia = SiblingPath(a.transform), ib = SiblingPath(b.transform);
            for (int i = 0; i < Mathf.Min(ia.Count, ib.Count); i++)
            {
                if (ia[i] != ib[i])
                {
                    return ia[i].CompareTo(ib[i]);
                }
            }

            // A parent comes before its children.
            return ia.Count.CompareTo(ib.Count);
        }

        private static int SceneOrder(GameObject go)
        {
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i) == go.scene)
                {
                    return i;
                }
            }

            // Prefab stage / prefab assets after the open scenes.
            return int.MaxValue;
        }

        /// <summary>Sibling indices from the root down to the object.</summary>
        private static List<int> SiblingPath(Transform t)
        {
            var path = new List<int>();
            for (Transform current = t; current != null; current = current.parent)
            {
                path.Insert(0, current.GetSiblingIndex());
            }

            return path;
        }

        private static string GetGameObjectPath(GameObject target)
        {
            var path = new StringBuilder();
            for (Transform current = target.transform; current != null; current = current.parent)
            {
                if (path.Length > 0)
                {
                    path.Insert(0, "/");
                }

                path.Insert(0, current.name);
            }

            return path.ToString();
        }
    }
}
