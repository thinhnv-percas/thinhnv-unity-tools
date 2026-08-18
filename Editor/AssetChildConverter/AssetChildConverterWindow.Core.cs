using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AssetChildConverter
{
    public partial class AssetChildConverterWindow
    {
        private class PendingChild
        {
            public Object Original;
            public Object Clone;
            public string OriginalPath;
        }

        /// <summary>
        /// Clones each child into <paramref name="parent"/>'s asset file, repoints existing
        /// references (if requested) while the originals are still valid, then removes the
        /// now-redundant originals.
        /// </summary>
        private void Convert(List<Object> children, Object parent, bool repointReferences)
        {
            string parentPath = AssetDatabase.GetAssetPath(parent);
            var pending = new List<PendingChild>();

            // Phase A: clone each child into the parent's asset file. Originals are left untouched
            // so any existing reference to them still resolves during Phase B.
            foreach (Object child in children)
            {
                Object clone = Object.Instantiate(child);
                clone.name = child.name;
                AssetDatabase.AddObjectToAsset(clone, parent);

                pending.Add(new PendingChild
                {
                    Original = child,
                    Clone = clone,
                    OriginalPath = AssetDatabase.GetAssetPath(child),
                });
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(parentPath);

            lastLog.Add($"Embedded {pending.Count} asset(s) into '{parentPath}'.");

            // Phase B: repoint references while originals are still loadable.
            if (repointReferences)
            {
                var remap = new Dictionary<Object, Object>();
                foreach (PendingChild p in pending)
                {
                    remap[p.Original] = p.Clone;
                }

                List<ReferenceHit> hits = RepointReferences(remap, scanScenes, scanPrefabs, scanScriptableObjects);
                int total = 0;
                foreach (ReferenceHit hit in hits)
                {
                    total += hit.Count;
                }

                lastLog.Add($"Repointed {total} reference(s) across {hits.Count} asset(s).");
            }

            // Phase C: remove the now-redundant originals.
            foreach (PendingChild p in pending)
            {
                Object[] siblings = AssetDatabase.LoadAllAssetsAtPath(p.OriginalPath);
                bool wasSoleObjectInFile = siblings.Length <= 1;

                if (wasSoleObjectInFile)
                {
                    AssetDatabase.DeleteAsset(p.OriginalPath);
                    lastLog.Add($"Deleted original file '{p.OriginalPath}' (was the only object in it).");
                }
                else
                {
                    AssetDatabase.RemoveObjectFromAsset(p.Original);
                    AssetDatabase.ImportAsset(p.OriginalPath);
                    lastLog.Add($"Removed '{p.Clone.name}' from '{p.OriginalPath}' (other objects remain there).");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (pending.Count > 0)
            {
                EditorGUIUtility.PingObject(pending[0].Clone);
                Selection.activeObject = pending[0].Clone;
            }

            EditorUtility.DisplayDialog(
                "Asset Child Converter",
                $"Converted {pending.Count} asset(s) into sub-asset(s) of '{parent.name}'.\n\n" +
                "If any reference wasn't repointed automatically, search the affected field manually — " +
                "the original asset's GUID no longer exists.",
                "OK");
        }
    }
}
