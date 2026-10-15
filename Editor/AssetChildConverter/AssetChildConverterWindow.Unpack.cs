using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AssetChildConverter
{
    /// <summary>
    /// The other direction: lists the sub-assets already inside the parent's file and lifts the selected
    /// ones back out into standalone asset files, repointing references the same way
    /// <see cref="Convert"/> does — so the parent's own list fields end up pointing at the new
    /// standalone assets rather than going missing.
    /// <para>
    /// Sub-assets of an <em>imported</em> asset (an FBX's meshes, a texture's sprites) are produced by that
    /// asset's importer, not stored in the file, so removing them never sticks — the next reimport puts
    /// them back. For those parents the tool copies out and refuses to remove, rather than pretending.
    /// </para>
    /// </summary>
    public partial class AssetChildConverterWindow
    {
        /// <summary>One sub-asset found inside the parent's file, with its tick state.</summary>
        private class SubAssetRow
        {
            public Object Asset;
            public bool Selected;

            /// <summary>Internal machinery (animator state machines and the like) rather than content.</summary>
            public bool Hidden;
        }

        private class PendingExtract
        {
            public Object Original;
            public Object Clone;
            public string CreatedPath;
        }

        private readonly List<SubAssetRow> scannedChildren = new();

        // Which parent the current scan belongs to; a mismatch re-runs the scan on the next repaint.
        private Object scannedParent;
        private bool scanValid;

        private bool followSelection = true;
        private bool showHiddenChildren;
        private bool removeAfterUnpack = true;
        private string unpackFolder = string.Empty;
        private Vector2 childrenScroll;

        /// <summary>Re-reads the parent's file whenever the parent changes, so the list is never stale.</summary>
        private void SyncScan()
        {
            if (scanValid && scannedParent == parentAsset) return;

            scannedChildren.Clear();
            scannedParent = parentAsset;
            scanValid = true;

            if (parentAsset == null || parentAsset is SceneAsset || parentAsset is DefaultAsset) return;

            string path = AssetDatabase.GetAssetPath(parentAsset);
            if (string.IsNullOrEmpty(path)) return;

            unpackFolder = string.IsNullOrEmpty(unpackFolder) ? Path.GetDirectoryName(path) : unpackFolder;

            Object main = AssetDatabase.LoadMainAssetAtPath(path);
            var selected = new HashSet<Object>(Selection.objects);
            foreach (Object obj in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (!IsListableSubAsset(obj, main)) continue;

                scannedChildren.Add(new SubAssetRow
                {
                    Asset = obj,
                    // Pre-tick whatever the user already had selected in the Project window.
                    Selected = selected.Contains(obj),
                    Hidden = (obj.hideFlags & HideFlags.HideInHierarchy) != 0,
                });
            }
        }

        /// <summary>
        /// A file's detachable sub-assets. <see cref="AssetDatabase.LoadAllAssetsAtPath"/> hands back
        /// everything stored in the file, which for a Prefab or an FBX means every GameObject and
        /// Component in the hierarchy — those are the file's contents, not sub-assets you can lift out, so
        /// listing them showed a prefab's whole component tree as if it were unpackable.
        /// </summary>
        private static bool IsListableSubAsset(Object obj, Object main)
        {
            return obj != null && obj != main && !(obj is GameObject) && !(obj is Component);
        }

        /// <summary>How many detachable sub-assets the file holds right now — used to prove a removal stuck.</summary>
        private static int CountSubAssets(string assetPath)
        {
            Object main = AssetDatabase.LoadMainAssetAtPath(assetPath);
            int count = 0;
            foreach (Object obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (IsListableSubAsset(obj, main)) count++;
            }

            return count;
        }

        /// <summary>Forces the next <see cref="SyncScan"/> to re-read the file.</summary>
        private void InvalidateScan()
        {
            scanValid = false;
        }

        private void OnSelectionChange()
        {
            if (followSelection)
            {
                Object selected = Selection.activeObject;
                if (selected != null && AssetDatabase.Contains(selected))
                {
                    // Selecting a sub-asset means "work on the file it lives in".
                    string path = AssetDatabase.GetAssetPath(selected);
                    Object main = AssetDatabase.LoadMainAssetAtPath(path);
                    parentAsset = main != null ? main : selected;
                    unpackFolder = Path.GetDirectoryName(path);
                }
            }

            InvalidateScan();
            Repaint();
        }

        private void DrawChildrenSection()
        {
            SyncScan();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Existing Sub-Assets ({CountVisible()})", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                followSelection = GUILayout.Toggle(followSelection, "Follow Selection", EditorStyles.miniButton, GUILayout.Width(110));
                if (GUILayout.Button("Rescan", EditorStyles.miniButton, GUILayout.Width(56))) InvalidateScan();
            }

            if (parentAsset == null)
            {
                EditorGUILayout.HelpBox("Pick a parent asset to list what is already inside it.", MessageType.None);
                return;
            }

            bool imported = IsImportedSource(parentAsset);
            if (imported)
            {
                EditorGUILayout.HelpBox(
                    $"'{parentAsset.name}' looks like an imported asset. Its sub-assets are regenerated by its " +
                    "importer, so removing them will most likely not survive the next reimport — the unpack will " +
                    "report whether it actually stuck.",
                    MessageType.Warning);
            }

            if (CountVisible() == 0)
            {
                EditorGUILayout.HelpBox(
                    scannedChildren.Count == 0
                        ? "This asset has no sub-assets."
                        : $"All {scannedChildren.Count} sub-asset(s) are hidden internal objects. Tick 'Show hidden' to see them.",
                    MessageType.None);
            }
            else
            {
                DrawChildrenList();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                bool wasShowingHidden = showHiddenChildren;
                showHiddenChildren = EditorGUILayout.ToggleLeft("Show hidden internal objects", showHiddenChildren);
                // Hidden rows must not stay ticked while invisible — that would unpack something unseen.
                if (wasShowingHidden && !showHiddenChildren) DeselectHidden();

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(40))) SetAllSelected(true);
                if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(46))) SetAllSelected(false);
            }

            DrawUnpackControls();
        }

        private void DrawChildrenList()
        {
            float height = Mathf.Min(CountVisible() * 20f + 8f, 160f);
            childrenScroll = EditorGUILayout.BeginScrollView(childrenScroll, EditorStyles.helpBox, GUILayout.Height(height));

            foreach (SubAssetRow row in scannedChildren)
            {
                if (row.Hidden && !showHiddenChildren) continue;
                if (row.Asset == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    row.Selected = EditorGUILayout.Toggle(row.Selected, GUILayout.Width(16));

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(row.Asset, row.Asset.GetType(), false);
                    }

                    GUILayout.Label(row.Hidden ? "hidden" : row.Asset.GetType().Name,
                        EditorStyles.miniLabel, GUILayout.Width(110));

                    if (GUILayout.Button("ping", EditorStyles.miniButton, GUILayout.Width(34)))
                    {
                        EditorGUIUtility.PingObject(row.Asset);
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// Whether the parent's file is a format Unity imports rather than serialises itself, using the same
        /// extension list the convert direction warns on. This drives a warning only: it used to also disable
        /// the remove toggle via AssetDatabase.IsForeignAsset, which reported every parent as foreign and left
        /// the toggle permanently greyed out. Removal is now always offered and the before/after count
        /// afterwards says whether it worked, which is the honest answer either way.
        /// </summary>
        private static bool IsImportedSource(Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return !string.IsNullOrEmpty(path) && ImportedSourceExtensions.Contains(Path.GetExtension(path));
        }

        private void DrawUnpackControls()
        {
            List<Object> chosen = GetSelectedChildren();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Unpack To", GUILayout.Width(66));
                EditorGUILayout.SelectableLabel(unpackFolder, EditorStyles.textField, GUILayout.Height(18));
                if (GUILayout.Button("Browse", EditorStyles.miniButton, GUILayout.Width(58))) BrowseUnpackFolder();
            }

            removeAfterUnpack = EditorGUILayout.ToggleLeft(
                "Remove from parent after extracting (leave off to copy out)", removeAfterUnpack);

            foreach (Object child in chosen)
            {
                if (!CanUnpack(child, out string reason))
                {
                    EditorGUILayout.HelpBox(reason, MessageType.Error);
                }
            }

            bool blocked = chosen.Count == 0 || string.IsNullOrEmpty(unpackFolder) || HasBlockedSelection(chosen);
            using (new EditorGUI.DisabledScope(blocked))
            {
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.75f, 0.3f);
                bool clicked = GUILayout.Button(
                    $"Unpack {chosen.Count} Sub-Asset(s) Out Of '{(parentAsset != null ? parentAsset.name : "?")}'",
                    GUILayout.Height(26));
                GUI.backgroundColor = prev;

                if (clicked && ConfirmUnpack(chosen.Count, removeAfterUnpack))
                {
                    lastLog.Clear();
                    Unpack(chosen, parentAsset, fixReferences, unpackFolder, removeAfterUnpack);
                    InvalidateScan();
                }
            }
        }

        private bool ConfirmUnpack(int count, bool remove)
        {
            string tail = remove
                ? $"and remove them from '{parentAsset.name}'"
                : $"leaving the originals inside '{parentAsset.name}'";

            return EditorUtility.DisplayDialog(
                "Unpack Sub-Assets",
                $"This will write {count} sub-asset(s) into '{unpackFolder}' as standalone files, {tail}.\n\n" +
                "Make sure your work is committed or saved before continuing.\n\nProceed?",
                "Unpack", "Cancel");
        }

        /// <summary>
        /// Mirror of <see cref="Convert"/>: create the standalone copies first, repoint references while the
        /// sub-assets are still valid, then remove them from the parent.
        /// </summary>
        private void Unpack(List<Object> children, Object parent, bool repointReferences, string folder, bool remove)
        {
            string parentPath = AssetDatabase.GetAssetPath(parent);
            int before = CountSubAssets(parentPath);
            var pending = new List<PendingExtract>();

            // Phase A: write each sub-asset out as its own file. The originals stay put so references still resolve.
            foreach (Object child in children)
            {
                Object clone = Object.Instantiate(child);
                clone.name = child.name;
                // Instantiate carries hideFlags over; a hidden standalone asset would be invisible in the Project window.
                clone.hideFlags = HideFlags.None;

                string fileName = SafeFileName(child.name, child.GetType().Name) + ExtensionFor(child);
                string createdPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, fileName).Replace('\\', '/'));
                AssetDatabase.CreateAsset(clone, createdPath);

                pending.Add(new PendingExtract { Original = child, Clone = clone, CreatedPath = createdPath });
                lastLog.Add($"Created '{createdPath}'.");
            }

            AssetDatabase.SaveAssets();

            // Phase B: repoint while the sub-assets still exist. This includes the parent's own fields, which
            // is the point — after unpacking they should address the new standalone asset.
            if (repointReferences)
            {
                var remap = new Dictionary<Object, Object>();
                foreach (PendingExtract p in pending)
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

            // Phase C: delete the originals out of the parent file.
            //
            // DestroyImmediate(obj, true) rather than AssetDatabase.RemoveObjectFromAsset: the latter only
            // detaches the object and leaves it alive, so it can be written straight back on the next save
            // and the sub-asset appears not to have gone anywhere. Destroying it is what the SubAssetEditor
            // in this package does, and it is what actually sticks.
            if (remove)
            {
                int destroyed = 0;
                foreach (PendingExtract p in pending)
                {
                    if (p.Original == null) continue;
                    Object.DestroyImmediate(p.Original, true);
                    destroyed++;
                }

                EditorUtility.SetDirty(parent);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(parentPath);
                lastLog.Add($"Deleted {destroyed} sub-asset(s) from '{parentPath}'.");
            }
            else
            {
                lastLog.Add($"Left the originals inside '{parentPath}' (copy-out only).");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Read the file back rather than assuming: a removal that silently failed is the whole reason
            // this reports numbers instead of just saying "done".
            int after = CountSubAssets(parentPath);
            lastLog.Add($"'{parentPath}' now holds {after} sub-asset(s) (was {before}).");
            bool removalStuck = !remove || after == before - pending.Count;

            if (pending.Count > 0)
            {
                Object first = AssetDatabase.LoadMainAssetAtPath(pending[0].CreatedPath);
                if (first != null)
                {
                    EditorGUIUtility.PingObject(first);
                    Selection.activeObject = first;
                }
            }

            string outcome = remove
                ? (removalStuck
                    ? $"and deleted them from '{parent.name}'."
                    : $"but '{parent.name}' still holds {after} sub-asset(s) — expected {before - pending.Count}. " +
                      "Check the Result log; the copies were still written.")
                : $"leaving the originals inside '{parent.name}'.";

            EditorUtility.DisplayDialog(
                "Asset Child Converter",
                $"Unpacked {pending.Count} sub-asset(s) into '{folder}' {outcome}",
                "OK");
        }

        private void BrowseUnpackFolder()
        {
            string start = string.IsNullOrEmpty(unpackFolder) ? "Assets" : unpackFolder;
            string picked = EditorUtility.OpenFolderPanel("Unpack Sub-Assets To", start, string.Empty);
            if (string.IsNullOrEmpty(picked)) return;

            string relative = ToProjectRelative(picked);
            if (relative == null)
            {
                EditorUtility.DisplayDialog("Asset Child Converter",
                    "Pick a folder inside this project's Assets folder.", "OK");
                return;
            }

            unpackFolder = relative;
        }

        /// <summary>Absolute path to an "Assets/..." path, or null when it falls outside the project.</summary>
        private static string ToProjectRelative(string absolute)
        {
            string root = Application.dataPath.Replace('\\', '/');
            string normalised = absolute.Replace('\\', '/');

            if (normalised == root) return "Assets";
            if (!normalised.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase)) return null;

            return "Assets" + normalised.Substring(root.Length);
        }

        /// <summary>Sub-asset names are free text; file names are not.</summary>
        private static string SafeFileName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name)) return fallback;

            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(System.Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }

            string cleaned = builder.ToString().Trim();
            return cleaned.Length == 0 ? fallback : cleaned;
        }

        /// <summary>
        /// Unity refuses some types outright as standalone files, and importer-owned ones only look like
        /// they extracted. PhysicMaterial is matched by name because it was renamed in newer Unity versions.
        /// </summary>
        private static string ExtensionFor(Object asset)
        {
            if (asset is Material) return ".mat";
            if (asset is AnimationClip) return ".anim";
            if (asset is RenderTexture) return ".renderTexture";

            switch (asset.GetType().Name)
            {
                case "PhysicMaterial":
                case "PhysicsMaterial":
                    return ".physicMaterial";
                case "PhysicsMaterial2D":
                    return ".physicsMaterial2D";
                default:
                    return ".asset";
            }
        }

        private static bool CanUnpack(Object child, out string reason)
        {
            reason = null;

            if (child is Sprite)
            {
                reason = $"'{child.name}' is a Sprite. Sprites belong to their source texture's importer — " +
                         "extract them with the Sprite Editor instead.";
                return false;
            }

            if (child is GameObject || child is Component)
            {
                reason = $"'{child.name}' is part of a prefab's hierarchy, not a detachable sub-asset.";
                return false;
            }

            if (child is MonoScript || child is SceneAsset || child is DefaultAsset)
            {
                reason = $"'{child.name}' is a {child.GetType().Name} and can't be written as a standalone asset.";
                return false;
            }

            return true;
        }

        private bool HasBlockedSelection(List<Object> chosen)
        {
            foreach (Object child in chosen)
            {
                if (!CanUnpack(child, out _)) return true;
            }

            return false;
        }

        private List<Object> GetSelectedChildren()
        {
            var result = new List<Object>();
            foreach (SubAssetRow row in scannedChildren)
            {
                if (row.Selected && row.Asset != null && (showHiddenChildren || !row.Hidden))
                {
                    result.Add(row.Asset);
                }
            }

            return result;
        }

        private int CountVisible()
        {
            int count = 0;
            foreach (SubAssetRow row in scannedChildren)
            {
                if (row.Asset != null && (showHiddenChildren || !row.Hidden)) count++;
            }

            return count;
        }

        private void SetAllSelected(bool value)
        {
            foreach (SubAssetRow row in scannedChildren)
            {
                if (showHiddenChildren || !row.Hidden) row.Selected = value;
            }
        }

        private void DeselectHidden()
        {
            foreach (SubAssetRow row in scannedChildren)
            {
                if (row.Hidden) row.Selected = false;
            }
        }
    }
}
