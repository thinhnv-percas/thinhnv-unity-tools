#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HierarchyCustom
{
    [InitializeOnLoad]
    static class HierarchyExpandCollapseToolbar
    {
        const string menuPath = "Tools/Thinhnv/Hierarchy Custom/Expand Collapse toolbar";
        const string prefsKey = "HierarchyCustom-expandCollapseToolbarEnabled";

        const float toolbarHeight = 20f;
        const float buttonSize = 18f;

        static bool enabled
        {
            get => EditorPrefs.GetBool(prefsKey, true);
            set => EditorPrefs.SetBool(prefsKey, value);
        }

        static HierarchyExpandCollapseToolbar()
        {
            EditorApplication.update -= UpdateAllWrapping;
            EditorApplication.update += UpdateAllWrapping;
        }

        [MenuItem(menuPath, false, 2)]
        static void ToggleEnabled()
        {
            enabled = !enabled;

            UpdateAllWrapping();
            EditorApplication.RepaintHierarchyWindow();
        }

        [MenuItem(menuPath, true, 2)]
        static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(menuPath, enabled);
            return true;
        }


        // patches each hierarchy window's own OnGUI (there's no public API to add a toolbar to it)
        // so we can push its content down and draw our two buttons in the freed-up space

        static void UpdateAllWrapping()
        {
            foreach (var window in GetHierarchyWindows())
                UpdateWrapping(window);
        }

        static void UpdateWrapping(EditorWindow window)
        {
            var hostView = GetFieldValue(window, "m_Parent");
            if (hostView == null) return;

            var currentDelegate = GetFieldValue(hostView, "m_OnGUI") as Delegate;
            var isWrapped = currentDelegate != null && currentDelegate.Method == mi_WrappedGUI;

            if (enabled && !isWrapped)
                SetFieldValue(hostView, "m_OnGUI", mi_WrappedGUI.CreateDelegate(t_EditorWindowDelegate, window));

            if (!enabled && isWrapped)
                SetFieldValue(hostView, "m_OnGUI", InvokeMethod(hostView, "CreateDelegate", "OnGUI"));

            if (enabled != isWrapped)
                window.Repaint();
        }

        static void WrappedGUI(EditorWindow window)
        {
            var originalPos = (Rect)GetFieldValue(window, "m_Pos");

            GUILayout.Space(0); // some third-party packages (e.g. GameAnalytics) read GUILayoutUtility.GetLastRect() right after OnGUI starts

            GUI.BeginGroup(new Rect(0, toolbarHeight, originalPos.width, originalPos.height - toolbarHeight));

            SetFieldValue(window, "m_Pos", new Rect(originalPos.x, originalPos.y + toolbarHeight, originalPos.width, originalPos.height - toolbarHeight));

            try
            {
                if (Event.current.type == EventType.MouseDown && new Rect(0, 0, originalPos.width, originalPos.height).Contains(Event.current.mousePosition))
                    SetStaticFieldValue(t_SceneHierarchyWindow, "s_LastInteractedHierarchy", window);

                InvokeMethod(window, "DoSceneHierarchy");
                InvokeMethod(window, "ExecuteCommands");
            }
            catch (TargetInvocationException exception)
            {
                // MethodInfo.Invoke wraps whatever the hierarchy throws (including Unity's own GUIUtility.ExitGUI control-flow exception) - rethrow the real one
                throw exception.InnerException ?? exception;
            }
            finally
            {
                SetFieldValue(window, "m_Pos", originalPos);
            }

            GUI.EndGroup();

            DrawToolbar(new Rect(0, 0, originalPos.width, toolbarHeight));
        }


        static void DrawToolbar(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, EditorStyles.toolbar);

            var buttonRect = new Rect(rect.xMax - buttonSize - 4, rect.y + (rect.height - buttonSize) / 2, buttonSize, buttonSize);

            DrawIconButton(buttonRect, collapseAllIcon, "Collapse All", () => SetExpandedRecursive(false));

            buttonRect.x -= buttonSize + 2;

            DrawIconButton(buttonRect, expandAllIcon, "Expand All", () => SetExpandedRecursive(true));
        }

        static void DrawIconButton(Rect rect, Texture icon, string tooltip, Action onClick)
        {
            var isHovered = rect.Contains(Event.current.mousePosition);

            if (Event.current.type == EventType.Repaint)
            {
                var opacity = (EditorGUIUtility.isProSkin ? .8f : .6f) * (isHovered ? 1.3f : 1f);

                var prevColor = GUI.color;
                GUI.color = new Color(1, 1, 1, Mathf.Clamp01(opacity));
                GUI.DrawTexture(rect, icon);
                GUI.color = prevColor;

                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }

            GUI.Label(rect, new GUIContent("", tooltip));

            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && isHovered)
            {
                Event.current.Use();
                onClick();
            }
        }


        static void SetExpandedRecursive(bool expand)
        {
            var setExpandedRecursive = FindMethod(t_SceneHierarchyWindow, "SetExpandedRecursive");
            if (setExpandedRecursive == null) return;

            foreach (var window in GetHierarchyWindows())
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.isLoaded) continue;

                    foreach (var root in scene.GetRootGameObjects())
                        setExpandedRecursive.Invoke(window, new object[] { root.GetInstanceID(), expand });
                }

                window.Repaint();
            }
        }


        // solid chevron icon, procedurally rasterized so the tool has no texture/package dependency

        static Texture2D expandAllIconCache;
        static Texture2D collapseAllIconCache;

        static Texture expandAllIcon => expandAllIconCache ??= ExpandCollapseIcons.CreateExpandTexture();
        static Texture collapseAllIcon => collapseAllIconCache ??= ExpandCollapseIcons.CreateCollapseTexture();

        static Texture2D CreateChevronTexture(int size, bool pointDown)
        {
            var margin = size * .16f;
            var halfThickness = size * .11f;

            var tipY = pointDown ? size - margin : margin;
            var armY = pointDown ? margin : size - margin;

            var tip = new Vector2(size / 2f, tipY);
            var armL = new Vector2(margin, armY);
            var armR = new Vector2(size - margin, armY);

            var pixels = new Color32[size * size];

            for (var designY = 0; designY < size; designY++)
            {
                var bufferRow = size - 1 - designY; // texture row 0 is the visual bottom, designY 0 is the visual top

                for (var x = 0; x < size; x++)
                {
                    var p = new Vector2(x + .5f, designY + .5f);

                    var distance = Mathf.Min(DistanceToSegment(p, tip, armL), DistanceToSegment(p, tip, armR));
                    var alpha = Mathf.Clamp01(halfThickness - distance + .5f);

                    pixels[bufferRow * size + x] = new Color(1, 1, 1, alpha);
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
            };

            texture.SetPixels32(pixels);
            texture.Apply();

            return texture;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + ab * t);
        }


        // minimal reflection helpers into Unity's internal hierarchy window/host view types

        const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Type t_SceneHierarchyWindow = typeof(EditorWindow).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
        static readonly Type t_HostView = typeof(EditorWindow).Assembly.GetType("UnityEditor.HostView");
        static readonly Type t_EditorWindowDelegate = t_HostView?.GetNestedType("EditorWindowDelegate", bindingFlags);
        static readonly MethodInfo mi_WrappedGUI = typeof(HierarchyExpandCollapseToolbar).GetMethod(nameof(WrappedGUI), bindingFlags);

        static IEnumerable GetHierarchyWindowList() => FindField(t_SceneHierarchyWindow, "s_SceneHierarchyWindows")?.GetValue(null) as IEnumerable;

        static IEnumerable<EditorWindow> GetHierarchyWindows()
        {
            var list = GetHierarchyWindowList();
            if (list == null) yield break;

            foreach (var item in list)
                if (item is EditorWindow window)
                    yield return window;
        }

        static FieldInfo FindField(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, bindingFlags);
                if (field != null) return field;
            }
            return null;
        }

        static MethodInfo FindMethod(Type type, string name)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                var method = t.GetMethod(name, bindingFlags);
                if (method != null) return method;
            }
            return null;
        }

        static object GetFieldValue(object target, string name) => FindField(target.GetType(), name)?.GetValue(target);
        static void SetFieldValue(object target, string name, object value) => FindField(target.GetType(), name)?.SetValue(target, value);
        static void SetStaticFieldValue(Type type, string name, object value) => FindField(type, name)?.SetValue(null, value);

        static object InvokeMethod(object target, string name, params object[] args) => FindMethod(target.GetType(), name)?.Invoke(target, args);
    }
}
#endif
