using UnityEditor;
using UnityEngine;
using System.Reflection;
using System.Linq;

[CanEditMultipleObjects]
[CustomEditor(typeof(MonoBehaviour), true)]
public class ContextMenuButtonEditor : Editor
{
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
