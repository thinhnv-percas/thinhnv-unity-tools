#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace HierarchyCustom
{
    [InitializeOnLoad]
    static class HierarchyComponentMinimap
    {
        const string menuPath = "Tools/Thinhnv/Hierarchy Custom/Component minimap";
        const string prefsKey = "HierarchyCustom-componentMinimapEnabled";

        static bool enabled
        {
            get => EditorPrefs.GetBool(prefsKey, true);
            set => EditorPrefs.SetBool(prefsKey, value);
        }

        static HierarchyComponentMinimap()
        {
            EditorApplication.hierarchyWindowItemOnGUI -= OnHierarchyItemGUI;
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGUI;
        }

        [MenuItem(menuPath, false, 1)]
        static void ToggleEnabled()
        {
            enabled = !enabled;
            EditorApplication.RepaintHierarchyWindow();
        }

        [MenuItem(menuPath, true, 1)]
        static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(menuPath, enabled);
            return true;
        }


        static bool mousePressed;

        static void OnHierarchyItemGUI(int instanceId, Rect selectionRect)
        {
            if (!enabled) return;

            if (!(EditorUtility.InstanceIDToObject(instanceId) is GameObject go)) return;

            var e = Event.current;
            var holdingAlt = e.alt;

            var fullRowRect = Rect.MinMaxRect(32, selectionRect.y, selectionRect.xMax + 16, selectionRect.yMax);
            var isRowHovered = Rect.MinMaxRect(fullRowRect.x - 32, fullRowRect.y, fullRowRect.xMax, fullRowRect.yMax).Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && isRowHovered) mousePressed = true;
            if (e.type == EventType.MouseUp || e.type == EventType.MouseLeaveWindow || e.type == EventType.DragPerform) mousePressed = false;


            void componentButton(Rect buttonRect, Component component)
            {
                var isHovered = buttonRect.Contains(e.mousePosition);

                if (e.type == EventType.Repaint)
                {
                    var icon = GetComponentIcon(component);

                    if (icon)
                    {
                        var isActive = (isHovered && holdingAlt) || (ComponentInspectorWindow.instance && ComponentInspectorWindow.instance.component == component);
                        var isPressed = isHovered && mousePressed;

                        var normalOpacity = EditorGUIUtility.isProSkin ? .47f : .7f;
                        var activeOpacity = 1f;
                        var pressedOpacity = EditorGUIUtility.isProSkin ? .65f : .9f;

                        var prevColor = GUI.color;
                        GUI.color = new Color(1, 1, 1, isActive ? (isPressed ? pressedOpacity : activeOpacity) : normalOpacity);

                        GUI.DrawTexture(new Rect(buttonRect.center.x - 6, buttonRect.center.y - 6, 12, 12), icon);

                        GUI.color = prevColor;
                    }
                }

                if (!holdingAlt) return;

                MarkInteractive(buttonRect); // keeps the hierarchy's own row click/drag from stealing alt-clicks meant for these icons

                if (!isHovered) return;

                if (e.type == EventType.MouseDown)
                    e.Use();

                if (e.type == EventType.MouseUp)
                {
                    e.Use();

                    if (ComponentInspectorWindow.instance && ComponentInspectorWindow.instance.component == component)
                        ComponentInspectorWindow.instance.Close();
                    else
                        ComponentInspectorWindow.Show(component, EditorGUIUtility.GUIToScreenPoint(new Vector2(selectionRect.xMax + 25, selectionRect.y)));
                }
            }


            if (isRowHovered && holdingAlt)
                componentButton(new Rect(fullRowRect.x + 1.5f, fullRowRect.y, 13, fullRowRect.height), go.transform);


            var buttonWidth = 13;
            var minButtonX = selectionRect.x + GUI.skin.label.CalcSize(new GUIContent(go.name)).x + buttonWidth + 2;
            var buttonRect = new Rect(fullRowRect.xMax - buttonWidth - 1.5f, fullRowRect.y, buttonWidth, fullRowRect.height);

            if (PrefabUtility.IsAnyPrefabInstanceRoot(go) && !PrefabUtility.IsPartOfModelPrefab(go))
                buttonRect.x -= 13;

            foreach (var component in go.GetComponents<Component>())
            {
                if (component is Transform) continue;
                if (buttonRect.x < minButtonX) break;

                componentButton(buttonRect, component);

                buttonRect.x -= buttonWidth;
            }
        }


        static Texture GetComponentIcon(Component component)
        {
            if (!component) return null;

            if (!componentIcons_byType.TryGetValue(component.GetType(), out var icon))
                componentIcons_byType[component.GetType()] = icon = EditorGUIUtility.ObjectContent(component, component.GetType()).image;

            return icon;
        }

        static readonly Dictionary<Type, Texture> componentIcons_byType = new();


        static void MarkInteractive(Rect rect)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (mi_UnclipToWindow == null || pi_GUIViewCurrent == null || mi_MarkHotRegion == null) return;

            var unclippedRect = (Rect)mi_UnclipToWindow.Invoke(null, new object[] { rect });
            var guiView = pi_GUIViewCurrent.GetValue(null);

            if (guiView != null)
                mi_MarkHotRegion.Invoke(guiView, new object[] { unclippedRect });
        }

        const BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        static readonly Type t_GUIView = typeof(Editor).Assembly.GetType("UnityEditor.GUIView");
        static readonly PropertyInfo pi_GUIViewCurrent = t_GUIView?.GetProperty("current", bindingFlags);
        static readonly MethodInfo mi_MarkHotRegion = t_GUIView?.GetMethod("MarkHotRegion", bindingFlags);
        static readonly MethodInfo mi_UnclipToWindow = typeof(GUI).Assembly.GetType("UnityEngine.GUIClip")?.GetMethod("UnclipToWindow", bindingFlags, null, new[] { typeof(Rect) }, null);
    }


    class ComponentInspectorWindow : EditorWindow
    {
        const string widthPrefsKey = "HierarchyCustom-componentWindowWidth";

        public Component component;
        Editor editor;

        Vector2 scroll;

        public static ComponentInspectorWindow instance;

        public static void Show(Component component, Vector2 screenPosition)
        {
            if (instance)
                instance.Close();

            instance = CreateInstance<ComponentInspectorWindow>();
            instance.component = component;
            instance.editor = Editor.CreateEditor(component);

            typeof(EditorWindow).GetMethod("ShowWithMode", BindingFlags.Instance | BindingFlags.NonPublic)
                                 .Invoke(instance, new object[] { 3 }); // ShowMode.NoShadow

            var width = Mathf.Max(EditorPrefs.GetFloat(widthPrefsKey, 320f), 280f);

            instance.position = new Rect(screenPosition.x, screenPosition.y, width, 300);
        }

        void OnGUI()
        {
            if (!component) { Close(); return; }

            EditorGUILayout.LabelField(ObjectNames.NicifyVariableName(component.GetType().Name), EditorStyles.boldLabel);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            editor.OnInspectorGUI();
            EditorGUILayout.EndScrollView();

            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                Close();
        }

        void OnLostFocus() => Close();

        void OnDestroy()
        {
            if (editor)
                DestroyImmediate(editor);

            EditorPrefs.SetFloat(widthPrefsKey, position.width);

            if (instance == this)
                instance = null;
        }
    }
}
#endif
