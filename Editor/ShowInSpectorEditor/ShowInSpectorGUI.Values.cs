using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only value drawing for the "Show In Spector" section: the counterpart of
/// <c>ContextMenuButtonGUI.DrawField</c>, except nothing here writes back. Every leaf widget is drawn
/// inside a disabled scope so it looks and behaves like an inspector field without being editable,
/// while collection foldouts stay interactive (a disabled foldout could never be opened).
/// Enumerables are walked lazily and capped, since an iterator member is re-read on every repaint.
/// </summary>
public static partial class ShowInSpectorGUI
{
    /// <summary>Only the foldout expand state lives here, keyed by member path + element index.</summary>
    private static readonly Dictionary<string, bool> FoldoutStates = new Dictionary<string, bool>();

    /// <summary>Enumeration cap: guards against long — or endless — iterator members.</summary>
    private const int MaxEnumerated = 256;

    private static void DrawValue(string label, Type declaredType, object value, bool mixed, string key)
    {
        if (mixed)
        {
            EditorGUILayout.LabelField(label, "—  (mixed values)");
            return;
        }

        Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        // A member declared as object/interface/abstract says nothing useful; the boxed value's own type does.
        if (value != null && (type == typeof(object) || type.IsInterface || type.IsAbstract))
        {
            type = value.GetType();
        }

        if (value == null && !typeof(UnityEngine.Object).IsAssignableFrom(type))
        {
            EditorGUILayout.LabelField(label, "null");
            return;
        }

        // Transform and friends implement IEnumerable, so Unity objects must never take the collection path.
        if (value is IEnumerable enumerable && !(value is string) && !(value is UnityEngine.Object))
        {
            DrawEnumerable(label, enumerable, key);
            return;
        }

        using (new EditorGUI.DisabledScope(true))
        {
            DrawLeaf(label, type, value);
        }
    }

    /// <summary>One non-collection value, drawn with the closest matching inspector widget.</summary>
    private static void DrawLeaf(string label, Type type, object value)
    {
        if (typeof(UnityEngine.Object).IsAssignableFrom(type))
        {
            EditorGUILayout.ObjectField(label, (UnityEngine.Object)value, type, true);
            return;
        }

        if (type == typeof(bool)) { EditorGUILayout.Toggle(label, (bool)value); return; }
        if (type == typeof(string) || type == typeof(char)) { EditorGUILayout.TextField(label, value.ToString()); return; }
        if (type == typeof(float)) { EditorGUILayout.FloatField(label, (float)value); return; }
        if (type == typeof(double)) { EditorGUILayout.DoubleField(label, (double)value); return; }

        if (type == typeof(int) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(byte) || type == typeof(sbyte))
        {
            EditorGUILayout.IntField(label, Convert.ToInt32(value));
            return;
        }
        if (type == typeof(long) || type == typeof(uint)) { EditorGUILayout.LongField(label, Convert.ToInt64(value)); return; }
        // ulong/decimal have no editor field wide enough to hold them; text keeps every digit.
        if (type == typeof(ulong) || type == typeof(decimal)) { EditorGUILayout.TextField(label, value.ToString()); return; }

        if (type == typeof(Vector2)) { EditorGUILayout.Vector2Field(label, (Vector2)value); return; }
        if (type == typeof(Vector3)) { EditorGUILayout.Vector3Field(label, (Vector3)value); return; }
        if (type == typeof(Vector4)) { EditorGUILayout.Vector4Field(label, (Vector4)value); return; }
        if (type == typeof(Vector2Int)) { EditorGUILayout.Vector2IntField(label, (Vector2Int)value); return; }
        if (type == typeof(Vector3Int)) { EditorGUILayout.Vector3IntField(label, (Vector3Int)value); return; }
        if (type == typeof(Quaternion))
        {
            Quaternion rotation = (Quaternion)value;
            EditorGUILayout.Vector4Field(label, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));
            return;
        }
        if (type == typeof(Color)) { EditorGUILayout.ColorField(label, (Color)value); return; }
        if (type == typeof(Color32)) { EditorGUILayout.ColorField(label, (Color32)value); return; }
        if (type == typeof(Rect)) { EditorGUILayout.RectField(label, (Rect)value); return; }
        if (type == typeof(RectInt)) { EditorGUILayout.RectIntField(label, (RectInt)value); return; }
        if (type == typeof(Bounds)) { EditorGUILayout.BoundsField(label, (Bounds)value); return; }
        if (type == typeof(BoundsInt)) { EditorGUILayout.BoundsIntField(label, (BoundsInt)value); return; }
        if (type == typeof(LayerMask)) { EditorGUILayout.IntField(label, ((LayerMask)value).value); return; }
        if (type == typeof(AnimationCurve)) { EditorGUILayout.CurveField(label, (AnimationCurve)value); return; }

        if (type.IsEnum)
        {
            if (type.IsDefined(typeof(FlagsAttribute), false)) EditorGUILayout.EnumFlagsField(label, (Enum)value);
            else EditorGUILayout.EnumPopup(label, (Enum)value);
            return;
        }

        EditorGUILayout.LabelField(label, value.ToString());
    }

    /// <summary>Foldout with the item count, then one recursive value per element (capped, see MaxEnumerated).</summary>
    private static void DrawEnumerable(string label, IEnumerable source, string key)
    {
        var items = new List<object>();
        bool truncated = false;
        try
        {
            foreach (object item in source)
            {
                if (items.Count >= MaxEnumerated)
                {
                    truncated = true;
                    break;
                }
                items.Add(item);
            }
        }
        catch (Exception exception)
        {
            EditorGUILayout.LabelField(label, $"⚠ {exception.GetType().Name}: {exception.Message}");
            return;
        }

        Type element = GetElementType(source.GetType()) ?? typeof(object);
        FoldoutStates.TryGetValue(key, out bool expanded);
        expanded = EditorGUILayout.Foldout(expanded,
            $"{label}  [{items.Count}{(truncated ? "+" : string.Empty)}] {element.Name}", true);
        FoldoutStates[key] = expanded;
        if (!expanded) return;

        using (new EditorGUI.IndentLevelScope())
        {
            for (int i = 0; i < items.Count; i++)
            {
                DrawValue($"Element {i}", element, items[i], false, $"{key}[{i}]");
            }
            if (truncated) EditorGUILayout.LabelField($"… stopped after {MaxEnumerated} items");
        }
    }

    /// <summary>Element type of an array or of any <see cref="IEnumerable{T}"/> the collection implements.</summary>
    private static Type GetElementType(Type collection)
    {
        if (collection.IsArray) return collection.GetElementType();

        if (collection.IsGenericType && collection.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return collection.GetGenericArguments()[0];
        }
        foreach (Type contract in collection.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return contract.GetGenericArguments()[0];
            }
        }
        return null;
    }
}
