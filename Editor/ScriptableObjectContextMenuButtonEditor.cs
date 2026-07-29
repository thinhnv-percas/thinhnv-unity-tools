using UnityEditor;
using UnityEngine;
using System.Reflection;
using System.Linq;

/// <summary>
/// Generic inspector for every <see cref="ScriptableObject"/>: draws the default
/// inspector, then a button for each method decorated with <see cref="ContextMenuAttribute"/>.
/// This lets any SO expose one-click editor actions (e.g. ObjectDefinitionSO's
/// "Apply Base Mass To Prefabs") without needing its own custom editor.
///
/// Only parameterless methods are supported (matching Unity's own ContextMenu rules).
/// A ContextMenu "validate" method, if present, disables the button when it returns false.
/// </summary>
[CanEditMultipleObjects]
[CustomEditor(typeof(ScriptableObject), true)]
public class ScriptableObjectContextMenuButtonEditor : Editor
{
    private const BindingFlags MethodFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<ContextMenu>() != null &&
                        m.GetParameters().Length == 0);

        if (methods.Any())
        {
            GUILayout.Space(6);
            Rect rect = EditorGUILayout.GetControlRect(false, 10);
            rect.height = 2;
            EditorGUI.DrawRect(rect, new Color32(0, 135, 189, 255));
            EditorGUILayout.LabelField("Context Menu", EditorStyles.boldLabel);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<ContextMenu>();

                if (GUILayout.Button(attr.menuItem))
                {
                    // Run on every selected object, not just the primary target, so the button
                    // honours [CanEditMultipleObjects] instead of affecting one object only.
                    foreach (var obj in targets)
                    {
                        method.Invoke(obj, null);

                        if (!Application.isPlaying)
                            EditorUtility.SetDirty(obj);
                    }
                }
            }
        }
    }
}
