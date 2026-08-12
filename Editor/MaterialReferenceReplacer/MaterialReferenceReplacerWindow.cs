using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.MaterialReferenceReplacer
{
    /// <summary>
    /// Finds every serialized reference to one or more "old" materials — across Scenes, Prefabs and
    /// ScriptableObject assets in <c>Assets/</c> — and repoints them at a single "original" material.
    ///
    /// Works generically over any <see cref="SerializedProperty"/> of type ObjectReference, so it
    /// catches Renderer.sharedMaterial(s) as well as any custom Material field on a MonoBehaviour or
    /// ScriptableObject (e.g. <c>ObjectDefinitionSO</c>, <c>GameConfigSO</c>).
    ///
    /// "Find References" is a read-only dry run; nothing is written to disk until "Replace" is
    /// confirmed. Scenes that aren't already open are opened additively, processed and closed again
    /// without disturbing whatever scene(s) you currently have loaded.
    ///
    /// Open via: Tools &gt; Thinhnv &gt; Material Reference Replacer.
    /// </summary>
    public partial class MaterialReferenceReplacerWindow : EditorWindow
    {
        private readonly List<Material> oldMaterials = new() { null };
        private Material newMaterial;

        private bool scanScenes = true;
        private bool scanPrefabs = true;
        private bool scanScriptableObjects = true;

        private List<ReferenceHit> lastHits = new();
        private bool hasScanned;
        private Vector2 resultsScroll;

        [MenuItem("Tools/Thinhnv/Material Reference Replacer")]
        public static void Open()
        {
            GetWindow<MaterialReferenceReplacerWindow>("Material Ref Replacer").minSize = new Vector2(420, 480);
        }

        private bool CanScan =>
            newMaterial != null
            && (scanScenes || scanPrefabs || scanScriptableObjects)
            && BuildTargetSet().Count > 0;

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Materials To Replace", EditorStyles.boldLabel);
            DrawDropZone();
            DrawOldMaterialsList();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Replace With (Material Gốc)", EditorStyles.boldLabel);
            newMaterial = (Material)EditorGUILayout.ObjectField(newMaterial, typeof(Material), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scan Scope", EditorStyles.boldLabel);
            scanScenes = EditorGUILayout.ToggleLeft("Scenes (Assets/**/*.unity)", scanScenes);
            scanPrefabs = EditorGUILayout.ToggleLeft("Prefabs (Assets/**/*.prefab)", scanPrefabs);
            scanScriptableObjects = EditorGUILayout.ToggleLeft("ScriptableObject Assets", scanScriptableObjects);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!CanScan))
            {
                if (GUILayout.Button("Find References", GUILayout.Height(28)))
                {
                    lastHits = Execute(apply: false);
                    hasScanned = true;
                }
            }

            if (hasScanned)
            {
                DrawResults();
                DrawReplaceButton();
            }
        }

        private void DrawOldMaterialsList()
        {
            for (int i = 0; i < oldMaterials.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    oldMaterials[i] = (Material)EditorGUILayout.ObjectField(oldMaterials[i], typeof(Material), false);
                    if (GUILayout.Button("x", GUILayout.Width(20)))
                    {
                        oldMaterials.RemoveAt(i);
                        i--;
                    }
                }
            }

            if (GUILayout.Button("+ Add Material Slot"))
            {
                oldMaterials.Add(null);
            }
        }

        private void DrawDropZone()
        {
            Rect dropArea = GUILayoutUtility.GetRect(0, 32, GUILayout.ExpandWidth(true));
            GUI.Box(dropArea, "Drag material(s) here to add", EditorStyles.helpBox);

            Event evt = Event.current;
            if (!dropArea.Contains(evt.mousePosition))
            {
                return;
            }

            if (evt.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = HasMaterialInDrag() ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                evt.Use();
            }
            else if (evt.type == EventType.DragPerform)
            {
                bool any = false;
                foreach (Object obj in DragAndDrop.objectReferences)
                {
                    if (obj is Material mat && !oldMaterials.Contains(mat))
                    {
                        oldMaterials.Add(mat);
                        any = true;
                    }
                }

                if (any)
                {
                    oldMaterials.RemoveAll(m => m == null);
                    DragAndDrop.AcceptDrag();
                }

                evt.Use();
            }
        }

        private static bool HasMaterialInDrag()
        {
            foreach (Object obj in DragAndDrop.objectReferences)
            {
                if (obj is Material)
                {
                    return true;
                }
            }

            return false;
        }

        private void DrawResults()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Found {TotalCount(lastHits)} reference(s) in {lastHits.Count} asset(s):", EditorStyles.boldLabel);

            resultsScroll = EditorGUILayout.BeginScrollView(resultsScroll, GUILayout.Height(160));
            foreach (ReferenceHit hit in lastHits)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"[{hit.Kind}]", GUILayout.Width(60));
                    if (GUILayout.Button(hit.AssetPath, EditorStyles.label))
                    {
                        PingAsset(hit.AssetPath);
                    }
                    EditorGUILayout.LabelField(hit.Count.ToString(), GUILayout.Width(30));
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawReplaceButton()
        {
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(lastHits.Count == 0))
            {
                Color prev = GUI.backgroundColor;
                GUI.backgroundColor = Color.red;
                bool clicked = GUILayout.Button(
                    $"Replace {TotalCount(lastHits)} Reference(s) In {lastHits.Count} Asset(s)",
                    GUILayout.Height(30));
                GUI.backgroundColor = prev;

                if (clicked && ConfirmReplace())
                {
                    lastHits = Execute(apply: true);
                    EditorUtility.DisplayDialog("Material Reference Replacer",
                        $"Replaced references in {lastHits.Count} asset(s).", "OK");
                }
            }
        }

        private bool ConfirmReplace()
        {
            return EditorUtility.DisplayDialog(
                "Replace Material References",
                "This will open/modify/save the affected Scenes, Prefabs and ScriptableObject assets.\n\n" +
                "Make sure your work is committed or saved before continuing.\n\nProceed?",
                "Replace", "Cancel");
        }

        private static void PingAsset(string path)
        {
            Object obj = AssetDatabase.LoadMainAssetAtPath(path);
            if (obj == null)
            {
                return;
            }

            EditorGUIUtility.PingObject(obj);
            Selection.activeObject = obj;
        }

        private static int TotalCount(List<ReferenceHit> hits)
        {
            int total = 0;
            foreach (ReferenceHit hit in hits)
            {
                total += hit.Count;
            }

            return total;
        }

        private HashSet<Material> BuildTargetSet()
        {
            var set = new HashSet<Material>();
            foreach (Material m in oldMaterials)
            {
                if (m != null && m != newMaterial)
                {
                    set.Add(m);
                }
            }

            return set;
        }
    }
}
