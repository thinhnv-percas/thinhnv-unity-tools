using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AssetChildConverter
{
    /// <summary>
    /// Embeds one or more existing assets as sub-assets ("children") of another asset's file —
    /// the same relationship a Sprite has to its source Texture, or a nested ScriptableObject
    /// has to its owning ScriptableObject.
    ///
    /// The source asset's serialized content is cloned into the target asset's file via
    /// <see cref="AssetDatabase.AddObjectToAsset"/>; the original standalone asset is then removed.
    /// Because a sub-asset shares its container's GUID, every existing serialized reference to the
    /// original asset would otherwise go missing — so before removing the original, this tool walks
    /// Scenes/Prefabs/ScriptableObjects and repoints any reference it finds at the new sub-asset.
    ///
    /// Works for native serialized types (ScriptableObject, Material, AnimationClip, PhysicMaterial,
    /// Mesh, Sprite created via script, etc). Imported source assets (Textures, Models, Audio, Fonts)
    /// don't apply here — their content is regenerated from the source file by their importer, not by
    /// this tool. Use the Texture's Sprite Editor / Sprite Mode to manage Sprite sub-assets instead.
    ///
    /// Open via: Tools &gt; Thinhnv &gt; Asset Child Converter.
    /// </summary>
    public partial class AssetChildConverterWindow : EditorWindow
    {
        private static readonly HashSet<string> ImportedSourceExtensions = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".exr", ".hdr",
            ".fbx", ".obj", ".dae", ".3ds", ".blend",
            ".wav", ".mp3", ".ogg", ".aiff",
            ".ttf", ".otf", ".unity", ".cs",
        };

        private Object parentAsset;
        private readonly List<Object> childAssets = new() { null };

        private bool fixReferences = true;
        private bool scanScenes = true;
        private bool scanPrefabs = true;
        private bool scanScriptableObjects = true;

        private readonly List<string> lastLog = new();
        private Vector2 logScroll;

        [MenuItem("Tools/Thinhnv/Asset Child Converter")]
        public static void Open()
        {
            var window = GetWindow<AssetChildConverterWindow>("Asset Child Converter");
            window.minSize = new Vector2(420, 420);
            window.Prefill();
        }

        private void Prefill()
        {
            if (parentAsset != null || childAssets.Count > 1)
            {
                return;
            }

            Object[] selection = Selection.objects;
            if (selection == null || selection.Length < 2)
            {
                return;
            }

            parentAsset = Selection.activeObject;
            childAssets.Clear();
            foreach (Object obj in selection)
            {
                if (obj != parentAsset)
                {
                    childAssets.Add(obj);
                }
            }

            if (childAssets.Count == 0)
            {
                childAssets.Add(null);
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Makes one or more assets a sub-asset (child) of another asset's file — e.g. a " +
                "ScriptableObject embedded inside another ScriptableObject. Existing references are " +
                "repointed automatically before the original standalone asset is removed.",
                MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Parent Asset (container)", EditorStyles.boldLabel);
            parentAsset = EditorGUILayout.ObjectField(parentAsset, typeof(Object), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Child Assets (will become sub-assets of Parent)", EditorStyles.boldLabel);
            DrawDropZone();
            DrawChildList();
            DrawImportedSourceWarning();

            EditorGUILayout.Space();
            fixReferences = EditorGUILayout.ToggleLeft(
                "Repoint existing references to the new sub-asset before removing the original", fixReferences);
            using (new EditorGUI.DisabledScope(!fixReferences))
            {
                EditorGUI.indentLevel++;
                scanScenes = EditorGUILayout.ToggleLeft("Scenes (Assets/**/*.unity)", scanScenes);
                scanPrefabs = EditorGUILayout.ToggleLeft("Prefabs (Assets/**/*.prefab)", scanPrefabs);
                scanScriptableObjects = EditorGUILayout.ToggleLeft("ScriptableObject / native asset files", scanScriptableObjects);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            DrawConvertButton();
            DrawLog();
        }

        private void DrawChildList()
        {
            for (int i = 0; i < childAssets.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    childAssets[i] = EditorGUILayout.ObjectField(childAssets[i], typeof(Object), false);
                    if (GUILayout.Button("x", GUILayout.Width(20)))
                    {
                        childAssets.RemoveAt(i);
                        i--;
                    }
                }
            }

            if (GUILayout.Button("+ Add Child Slot"))
            {
                childAssets.Add(null);
            }
        }

        private void DrawDropZone()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0, 32, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "Drag asset(s) here to add as children", EditorStyles.helpBox);

            Event evt = Event.current;
            if (!dropArea.Contains(evt.mousePosition))
            {
                return;
            }

            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDrop.objectReferences.Length > 0
                    ? DragAndDropVisualMode.Copy
                    : DragAndDropVisualMode.Rejected;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                bool any = false;
                foreach (Object obj in DragAndDrop.objectReferences)
                {
                    if (!childAssets.Contains(obj))
                    {
                        childAssets.Add(obj);
                        any = true;
                    }
                }

                if (any)
                {
                    childAssets.RemoveAll(o => o == null);
                    DragAndDrop.AcceptDrag();
                }

                evt.Use();
            }
        }

        private void DrawImportedSourceWarning()
        {
            foreach (Object child in childAssets)
            {
                if (child == null)
                {
                    continue;
                }

                string path = AssetDatabase.GetAssetPath(child);
                if (ImportedSourceExtensions.Contains(Path.GetExtension(path)))
                {
                    EditorGUILayout.HelpBox(
                        $"'{child.name}' looks like an imported source asset ({Path.GetExtension(path)}). " +
                        "Its content is regenerated by its importer, so embedding a clone of it as a sub-asset " +
                        "usually isn't what you want. Proceed only if you know this is a script-created asset.",
                        MessageType.Warning);
                }
            }
        }

        private void DrawConvertButton()
        {
            List<Object> validChildren = GetValidChildren(out List<string> reasons);

            foreach (string reason in reasons)
            {
                EditorGUILayout.HelpBox(reason, MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(parentAsset == null || validChildren.Count == 0))
            {
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = Color.red;
                bool clicked = GUILayout.Button(
                    $"Convert {validChildren.Count} Asset(s) To Sub-Asset(s) Of '{(parentAsset != null ? parentAsset.name : "?")}'",
                    GUILayout.Height(30));
                GUI.backgroundColor = prev;

                if (clicked && ConfirmConvert(validChildren.Count))
                {
                    lastLog.Clear();
                    Convert(validChildren, parentAsset, fixReferences);
                }
            }
        }

        private bool ConfirmConvert(int count)
        {
            return EditorUtility.DisplayDialog(
                "Convert To Sub-Asset",
                $"This will move {count} asset(s) into '{parentAsset.name}' and delete the original " +
                "standalone asset file(s). Make sure your work is committed or saved before continuing.\n\nProceed?",
                "Convert", "Cancel");
        }

        private void DrawLog()
        {
            if (lastLog.Count == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Result", EditorStyles.boldLabel);
            logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.Height(120));
            foreach (string line in lastLog)
            {
                EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        private List<Object> GetValidChildren(out List<string> reasons)
        {
            var result = new List<Object>();
            reasons = new List<string>();
            var seen = new HashSet<Object>();

            foreach (Object child in childAssets)
            {
                if (child == null || !seen.Add(child))
                {
                    continue;
                }

                if (!IsValidChild(child, parentAsset, out string reason))
                {
                    if (reason != null)
                    {
                        reasons.Add(reason);
                    }

                    continue;
                }

                result.Add(child);
            }

            return result;
        }

        private static bool IsValidChild(Object child, Object parent, out string reason)
        {
            reason = null;

            if (!AssetDatabase.Contains(child))
            {
                reason = $"'{child.name}' is not a project asset.";
                return false;
            }

            if (child is GameObject || child is Component)
            {
                reason = $"'{child.name}' is a GameObject/Component and can't be embedded as a sub-asset.";
                return false;
            }

            if (child is SceneAsset || child is MonoScript || child is DefaultAsset)
            {
                reason = $"'{child.name}' is a {child.GetType().Name} and can't be embedded as a sub-asset.";
                return false;
            }

            if (parent == null)
            {
                return true;
            }

            if (parent is SceneAsset || parent is DefaultAsset)
            {
                reason = $"'{parent.name}' ({parent.GetType().Name}) can't hold sub-assets — pick a native asset file (ScriptableObject, Material, etc.) as parent.";
                return false;
            }

            if (child == parent)
            {
                reason = $"'{child.name}' can't be its own parent.";
                return false;
            }

            if (!AssetDatabase.Contains(parent))
            {
                return true;
            }

            if (AssetDatabase.GetAssetPath(child) == AssetDatabase.GetAssetPath(parent))
            {
                reason = $"'{child.name}' is already in the same asset file as '{parent.name}'.";
                return false;
            }

            return true;
        }
    }
}
