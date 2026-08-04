using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Field drawing for ContextMenu button parameters: one editor field per supported primitive /
/// struct / <see cref="UnityEngine.Object"/> type, plus resizable foldouts for single-rank arrays
/// and <see cref="List{T}"/> (nested collections included, since elements recurse through
/// <see cref="DrawField"/>). Values live in the caller's arg cache; only the foldout expand state
/// is kept here, keyed by the parameter path so two methods never share a toggle.
/// </summary>
public static partial class ContextMenuButtonGUI
{
    private static readonly Dictionary<string, bool> FoldoutStates = new Dictionary<string, bool>();

    /// <summary>Draw an editor field for a single parameter value; unsupported types are shown read-only.</summary>
    /// <param name="key">Unique path for this value, used to persist collection foldout state.</param>
    private static object DrawField(string label, Type type, object value, string key)
    {
        if (type == typeof(int)) return EditorGUILayout.IntField(label, (int)(value ?? 0));
        if (type == typeof(long)) return EditorGUILayout.LongField(label, (long)(value ?? 0L));
        if (type == typeof(float)) return EditorGUILayout.FloatField(label, (float)(value ?? 0f));
        if (type == typeof(double)) return EditorGUILayout.DoubleField(label, (double)(value ?? 0d));
        if (type == typeof(bool)) return EditorGUILayout.Toggle(label, (bool)(value ?? false));
        if (type == typeof(string)) return EditorGUILayout.TextField(label, (string)value ?? string.Empty);
        if (type == typeof(Vector2Int)) return EditorGUILayout.Vector2IntField(label, (Vector2Int)(value ?? Vector2Int.zero));
        if (type == typeof(Vector3Int)) return EditorGUILayout.Vector3IntField(label, (Vector3Int)(value ?? Vector3Int.zero));
        if (type == typeof(Vector2)) return EditorGUILayout.Vector2Field(label, (Vector2)(value ?? Vector2.zero));
        if (type == typeof(Vector3)) return EditorGUILayout.Vector3Field(label, (Vector3)(value ?? Vector3.zero));
        if (type == typeof(Vector4)) return EditorGUILayout.Vector4Field(label, (Vector4)(value ?? Vector4.zero));
        if (type == typeof(Color)) return EditorGUILayout.ColorField(label, (Color)(value ?? Color.white));
        if (type == typeof(Bounds)) return EditorGUILayout.BoundsField(label, (Bounds)(value ?? default(Bounds)));
        if (type == typeof(BoundsInt)) return EditorGUILayout.BoundsIntField(label, (BoundsInt)(value ?? default(BoundsInt)));
        if (type == typeof(Rect)) return EditorGUILayout.RectField(label, (Rect)(value ?? default(Rect)));
        if (type == typeof(RectInt)) return EditorGUILayout.RectIntField(label, (RectInt)(value ?? default(RectInt)));
        if (type == typeof(AnimationCurve)) return EditorGUILayout.CurveField(label, (AnimationCurve)value ?? new AnimationCurve());
        if (type.IsEnum) return EditorGUILayout.EnumPopup(label, (Enum)(value ?? Enum.GetValues(type).GetValue(0)));
        if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            return EditorGUILayout.ObjectField(label, (UnityEngine.Object)value, type, true);
        if (TryGetElementType(type, out Type element, out bool isArray))
            return DrawCollection(label, element, isArray, value, key);

        EditorGUILayout.LabelField(label, $"({type.Name}) unsupported");
        return value;
    }

    /// <summary>True for <c>T[]</c> (rank 1) and <c>List&lt;T&gt;</c>, the two collection shapes we can build back.</summary>
    private static bool TryGetElementType(Type type, out Type element, out bool isArray)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            element = type.GetElementType();
            isArray = true;
            return true;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            element = type.GetGenericArguments()[0];
            isArray = false;
            return true;
        }
        element = null;
        isArray = false;
        return false;
    }

    /// <summary>Foldout + size field + one recursive field per element. Returns the (possibly new) collection.</summary>
    private static object DrawCollection(string label, Type element, bool isArray, object value, string key)
    {
        IList list = value as IList ?? CreateCollection(element, isArray, 0);
        FoldoutStates.TryGetValue(key, out bool expanded);

        using (new EditorGUILayout.HorizontalScope())
        {
            expanded = EditorGUILayout.Foldout(expanded, $"{label}  [{list.Count}] {element.Name}", true);
            // Delayed so typing a multi-digit size doesn't resize on every keystroke.
            int size = Mathf.Max(0, EditorGUILayout.DelayedIntField(list.Count, GUILayout.Width(56)));
            if (size != list.Count) list = Resize(list, element, isArray, size);
        }
        FoldoutStates[key] = expanded;
        if (!expanded) return list;

        using (new EditorGUI.IndentLevelScope())
        {
            for (int i = 0; i < list.Count; i++)
            {
                list[i] = DrawField($"Element {i}", element, list[i], $"{key}[{i}]");
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+", GUILayout.Width(24))) list = Resize(list, element, isArray, list.Count + 1);
                using (new EditorGUI.DisabledScope(list.Count == 0))
                {
                    if (GUILayout.Button("-", GUILayout.Width(24))) list = Resize(list, element, isArray, list.Count - 1);
                }
            }
        }
        return list;
    }

    /// <summary>New <c>element[]</c> or <c>List&lt;element&gt;</c> of <paramref name="count"/> default items.</summary>
    private static IList CreateCollection(Type element, bool isArray, int count)
    {
        if (isArray) return Array.CreateInstance(element, count);

        IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element));
        for (int i = 0; i < count; i++) list.Add(DefaultValue(element));
        return list;
    }

    /// <summary>Grow/shrink in place for a List; arrays are reallocated and copied, so use the return value.</summary>
    private static IList Resize(IList current, Type element, bool isArray, int size)
    {
        if (!isArray)
        {
            while (current.Count > size) current.RemoveAt(current.Count - 1);
            while (current.Count < size) current.Add(DefaultValue(element));
            return current;
        }

        IList resized = CreateCollection(element, true, size);
        for (int i = 0; i < Mathf.Min(current.Count, size); i++) resized[i] = current[i];
        return resized;
    }

    private static object DefaultValue(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}
