using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shared drawing for the ContextMenu button editors: renders a button for every
/// <see cref="ContextMenu"/> method on the target — including methods WITH parameters,
/// whose arguments are drawn as inspector fields (cached per method) and passed on click.
/// Arrays and <see cref="List{T}"/> parameters get a resizable foldout (see the .Fields partial).
/// Each invoke runs on every selected object so buttons honour <c>[CanEditMultipleObjects]</c>.
/// </summary>
public static partial class ContextMenuButtonGUI
{
    private const BindingFlags MethodFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Draw(UnityEngine.Object[] targets, Dictionary<string, object[]> argCache)
    {
        UnityEngine.Object primary = targets != null && targets.Length > 0 ? targets[0] : null;
        if (primary == null) return;

        // ref/out params can't be filled from boxed args, so skip those methods.
        var methods = primary.GetType()
            .GetMethods(MethodFlags)
            .Where(m => m.GetCustomAttribute<ContextMenu>() != null &&
                        !m.GetParameters().Any(p => p.ParameterType.IsByRef))
            .ToList();
        if (methods.Count == 0) return;

        GUILayout.Space(6);
        Rect rect = EditorGUILayout.GetControlRect(false, 10);
        rect.height = 2;
        EditorGUI.DrawRect(rect, new Color32(0, 135, 189, 255));
        EditorGUILayout.LabelField("Context Menu", EditorStyles.boldLabel);

        foreach (MethodInfo method in methods)
        {
            DrawMethod(method, targets, argCache);
        }
    }

    private static void DrawMethod(MethodInfo method, UnityEngine.Object[] targets, Dictionary<string, object[]> argCache)
    {
        ParameterInfo[] parameters = method.GetParameters();
        string label = method.GetCustomAttribute<ContextMenu>().menuItem;

        if (parameters.Length == 0)
        {
            if (GUILayout.Button(label)) Invoke(method, targets, null);
            return;
        }

        // Parameterized: draw a field per parameter (values cached across repaints), then invoke with them.
        string methodKey = method.ToString();
        object[] args = GetArgs(method, parameters, argCache);
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = DrawField(ObjectNames.NicifyVariableName(parameters[i].Name), parameters[i].ParameterType,
                    args[i], $"{methodKey}.{parameters[i].Name}");
            }
            if (GUILayout.Button($"Invoke {label}")) Invoke(method, targets, args);
        }
    }

    /// <summary>Fetch (or lazily build) the boxed argument array for a method, keyed by its full signature.</summary>
    private static object[] GetArgs(MethodInfo method, ParameterInfo[] parameters, Dictionary<string, object[]> argCache)
    {
        string key = method.ToString();
        if (!argCache.TryGetValue(key, out object[] args) || args.Length != parameters.Length)
        {
            args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : DefaultValue(parameters[i].ParameterType);
            }
            argCache[key] = args;
        }
        return args;
    }

    private static void Invoke(MethodInfo method, UnityEngine.Object[] targets, object[] args)
    {
        foreach (UnityEngine.Object obj in targets)
        {
            method.Invoke(obj, args);
            if (!Application.isPlaying) EditorUtility.SetDirty(obj);
        }
    }
}
