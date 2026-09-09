using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Read-only rendering of a single value that has no internal structure worth expanding: numerics, strings,
/// enums, the Unity math structs, <see cref="UnityEngine.Object"/> references, curves. Every widget is drawn
/// inside a disabled scope so it reads like an inspector field without being editable.
/// <para>
/// <see cref="IsLeaf"/> is the dividing line the viewer uses: a leaf is drawn here, anything else is a
/// collection or an object to drill into. <see cref="UnityEngine.Object"/> counts as a leaf on purpose — an
/// object field is clickable, so the reference stays reachable, and it stops the tree from wandering off
/// through <c>Transform.parent</c> into the rest of the scene.
/// </para>
/// </summary>
public static class MemberValueGUI
{
    private static readonly HashSet<Type> LeafTypes = new HashSet<Type>
    {
        typeof(string), typeof(char), typeof(bool), typeof(decimal),
        typeof(Vector2), typeof(Vector3), typeof(Vector4),
        typeof(Vector2Int), typeof(Vector3Int), typeof(Quaternion),
        typeof(Color), typeof(Color32), typeof(Rect), typeof(RectInt),
        typeof(Bounds), typeof(BoundsInt), typeof(LayerMask), typeof(AnimationCurve)
    };

    /// <summary>True when <see cref="DrawLeaf"/> can render the type on one line, structure and all.</summary>
    public static bool IsLeaf(Type type)
    {
        if (type == null) return true;
        Type resolved = Nullable.GetUnderlyingType(type) ?? type;
        return resolved.IsPrimitive || resolved.IsEnum || LeafTypes.Contains(resolved) ||
               typeof(UnityEngine.Object).IsAssignableFrom(resolved);
    }

    /// <summary>A plain <c>label: text</c> row, for nulls, errors and anything the viewer will not read.</summary>
    public static void DrawNote(string label, string note)
    {
        EditorGUILayout.LabelField(label, note);
    }

    /// <summary>Draw one leaf value with the closest matching inspector widget, disabled.</summary>
    public static void DrawLeaf(string label, Type declaredType, object value)
    {
        Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType ?? typeof(object);

        if (value == null)
        {
            // A null object reference still deserves the object field, which names the expected type.
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField(label, null, type, true);
            }
            else
            {
                DrawNote(label, "null");
            }
            return;
        }

        using (new EditorGUI.DisabledScope(true))
        {
            Draw(label, value.GetType(), value);
        }
    }

    private static void Draw(string label, Type type, object value)
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
        // ulong and decimal outrun every numeric field; text keeps every digit.
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

        // IntPtr and friends: no widget, but ToString still says something useful.
        EditorGUILayout.LabelField(label, value.ToString());
    }
}
