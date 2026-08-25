using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal; // Project already depends on URP (confirmed via PackageCache).
                                        // Remove this + the URP check below if this file needs to run
                                        // in a non-URP project too.

/// <summary>
/// Exposes Sorting Layer and Order in Layer on MeshRenderer in the Inspector,
/// while preserving the built-in Materials / Lighting / Probes UI, AND the
/// "2D > Mask Interaction" section that URP's 2D Renderer normally adds.
///
/// ── Why this file looks the way it does ─────────────────────────────────────
/// Unity's MeshRenderer inspector is driven by `UnityEditor.MeshRendererEditor`
/// (core, internal). When URP's 2D Renderer is active, URP additionally
/// registers `UnityEditor.Rendering.Universal.Renderer2DMeshEditor :
/// MeshRendererEditor` (also internal) for `[CustomEditor(typeof(MeshRenderer))]`,
/// which layers a "2D > Mask Interaction" foldout on top.
///
/// Only ONE class may own `[CustomEditor(typeof(MeshRenderer))]` at a time, so
/// this script and URP's Renderer2DMeshEditor collide — whichever Unity
/// resolves silently wins, the other is never invoked (no error, the Sorting
/// fields just never appear).
///
/// Rather than reflecting into URP's *internal* Renderer2DMeshEditor (fragile:
/// tied to an exact assembly name that can change between URP versions), this
/// script:
///   1. Reflects into core `UnityEditor.MeshRendererEditor` only — far more
///      stable, since URP's own Renderer2DMeshEditor is itself built on top
///      of it, so if Unity ever renames/removes it, URP's official code
///      breaks too (this is about as safe a reflection target as exists).
///   2. Re-implements the "2D > Mask Interaction" section directly, using
///      the PUBLIC `Renderer.maskInteraction` property (inherited by
///      MeshRenderer from the base Renderer class) — no reflection needed
///      for this part at all.
///   3. Adds its own Sorting Layer / Order in Layer section.
///
/// No override of CreateInspectorGUI(): when an Editor doesn't override it,
/// Unity 6's UIToolkit inspector automatically wraps OnInspectorGUI() (IMGUI)
/// in an IMGUIContainer for you, so one code path covers 2019.4 LTS → 6.x.
///
/// Compatible: Unity 2019.4 LTS → Unity 6.x (requires URP for the 2D section;
/// see the "URP dependency" note above if this needs to run without URP).
/// </summary>
[CustomEditor(typeof(MeshRenderer))]
[CanEditMultipleObjects]
public class MeshRendererSortingURPEditor : Editor
{
    private Editor _wrappedEditor;
    private string _2dFoldoutPrefsKey;
    private bool _2dFoldoutValue;

    private static class Styles
    {
        public static readonly GUIContent maskInteractionLabel =
            EditorGUIUtility.TrTextContent("Mask Interaction", "Renderer's interaction with a Sprite Mask");
    }

    private void OnEnable()
    {
        _wrappedEditor = CreateWrappedCoreEditor();

        // SavedBool (used internally by Unity for this exact purpose) is an
        // `internal` class, same restriction as MeshRendererEditor — so we
        // replicate its behavior with plain EditorPrefs instead.
        _2dFoldoutPrefsKey = $"{target.GetType()}.MeshRendererSortingEditor.2DFoldout";
        _2dFoldoutValue = EditorPrefs.GetBool(_2dFoldoutPrefsKey, true);
    }

    private void OnDisable()
    {
        // Editors created via Editor.CreateEditor must be destroyed manually;
        // the Inspector doesn't know about (and won't clean up) this instance
        // since it's owned by us, not by Unity's editor cache.
        if (_wrappedEditor != null)
        {
            DestroyImmediate(_wrappedEditor);
            _wrappedEditor = null;
        }
    }

    private Editor CreateWrappedCoreEditor()
    {
        try
        {
            // Core MeshRendererEditor lives in the same assembly as UnityEditor.Editor.
            Type coreEditorType = typeof(Editor).Assembly.GetType("UnityEditor.MeshRendererEditor");
            if (coreEditorType == null) return null;

            return Editor.CreateEditor(targets, coreEditorType);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MeshRendererSortingEditor] Could not wrap the built-in MeshRenderer editor, falling back to generic inspector. Reason: {e.Message}");
            return null;
        }
    }

    public override void OnInspectorGUI()
    {
        if (_wrappedEditor != null)
            _wrappedEditor.OnInspectorGUI();
        else
            base.OnInspectorGUI(); // fallback so the Inspector is never blank

        DrawMaskInteractionSection();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Sorting", EditorStyles.boldLabel);
        DrawSortingControls();
    }

    // ─── 2D > Mask Interaction (mirrors Renderer2DMeshEditor's behavior) ───────
    // `maskInteraction` is only a public property on SpriteRenderer / LineRenderer /
    // ParticleSystemRenderer — NOT on the base Renderer class, so MeshRenderer
    // doesn't expose it directly. It only exists as the internal serialized field
    // "m_MaskInteraction", so we reach it the same way Renderer2DMeshEditor itself
    // does: via SerializedProperty. FindProperty works by field name regardless of
    // that field's C# accessibility, so this doesn't hit the internal-access wall.
    private void DrawMaskInteractionSection()
    {
        var rpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (rpAsset == null) return; // Not on URP: core MeshRendererEditor already covers everything.

        var maskInteractionProp = serializedObject.FindProperty("m_MaskInteraction");
        if (maskInteractionProp == null) return; // Field renamed/removed in this Unity version.

        EditorGUI.BeginChangeCheck();
        bool newFoldoutValue = EditorGUILayout.Foldout(_2dFoldoutValue, "2D");
        if (EditorGUI.EndChangeCheck())
        {
            _2dFoldoutValue = newFoldoutValue;
            EditorPrefs.SetBool(_2dFoldoutPrefsKey, newFoldoutValue);
        }
        if (!_2dFoldoutValue) return;

        EditorGUI.indentLevel++;
        serializedObject.Update();
        EditorGUILayout.PropertyField(maskInteractionProp, Styles.maskInteractionLabel);
        serializedObject.ApplyModifiedProperties();
        EditorGUI.indentLevel--;
    }

    // ─── Sorting Layer / Order in Layer ────────────────────────────────────────
    // Uses the public Renderer API (sortingLayerID / sortingOrder) + Undo.RecordObjects
    // instead of FindProperty("m_SortingLayerID") to avoid relying on internal
    // serialized field names that can silently change between Unity versions.
    private void DrawSortingControls()
    {
        var renderers = targets.OfType<MeshRenderer>().ToArray();
        if (renderers.Length == 0) return;

        SortingLayer[] layers = SortingLayer.layers;
        if (layers == null || layers.Length == 0) return;

        string[] names = layers.Select(l => l.name).ToArray();

        try
        {
            // ── Sorting Layer ──────────────────────────────────────────────
            int firstID = renderers[0].sortingLayerID;
            if (!SortingLayer.IsValid(firstID)) firstID = layers[0].id;

            int firstIndex = Mathf.Clamp(
                SortingLayer.GetLayerValueFromID(firstID), 0, layers.Length - 1);

            bool layerMixed = renderers.Skip(1).Any(r => r.sortingLayerID != firstID);

            EditorGUI.showMixedValue = layerMixed;
            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup("Sorting Layer", firstIndex, names);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObjects(renderers, "Change Sorting Layer");
                int newID = layers[newIndex].id;
                foreach (var r in renderers)
                {
                    r.sortingLayerID = newID;
                    EditorUtility.SetDirty(r);
                }
            }
            EditorGUI.showMixedValue = false;

            // ── Order in Layer ──────────────────────────────────────────────
            int firstOrder = renderers[0].sortingOrder;
            bool orderMixed = renderers.Skip(1).Any(r => r.sortingOrder != firstOrder);

            EditorGUI.showMixedValue = orderMixed;
            EditorGUI.BeginChangeCheck();
            int newOrder = EditorGUILayout.IntField("Order in Layer", firstOrder);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObjects(renderers, "Change Sorting Order");
                foreach (var r in renderers)
                {
                    r.sortingOrder = newOrder;
                    EditorUtility.SetDirty(r);
                }
            }
        }
        finally
        {
            // Guaranteed reset even if a Popup/IntField call throws mid-draw,
            // so a transient error here can't leave every later field in the
            // Inspector permanently showing as "mixed value".
            EditorGUI.showMixedValue = false;
        }
    }
}

// This is free and unencumbered software released into the public domain.
//
// Anyone is free to copy, modify, publish, use, compile, sell, or
// distribute this software, either in source code form or as a compiled
// binary, for any purpose, commercial or non-commercial, and by any
// means.
//
// In jurisdictions that recognize copyright laws, the author or authors
// of this software dedicate any and all copyright interest in the
// software to the public domain. We make this dedication for the benefit
// of the public at large and to the detriment of our heirs and
// successors. We intend this dedication to be an overt act of
// relinquishment in perpetuity of all present and future rights to this
// software under copyright law.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
// IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
// OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
// ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
//
// For more information, please refer to <http://unlicense.org/>