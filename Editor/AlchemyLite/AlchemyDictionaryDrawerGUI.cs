using System;
using System.Collections;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Draws a live key/value list for every <see cref="AlchemyDictionaryAttribute"/> field on a
    /// target: read-only while not playing, editable while Play mode is running. Nothing here is
    /// written back through <c>SerializedProperty</c>/Undo — it edits the live Dictionary instance
    /// directly via reflection, since the whole point is a transient debug view, not persistence.
    /// </summary>
    internal static class AlchemyDictionaryDrawerGUI
    {
        public static void DrawAll(Object target, FieldInfo[] fields)
        {
            if (fields.Length == 0)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(
                Application.isPlaying ? "Alchemy Dictionaries" : "Alchemy Dictionaries (read-only — enter Play mode to edit)",
                EditorStyles.boldLabel);

            foreach (FieldInfo field in fields)
            {
                DrawField(target, field);
            }
        }

        private static void DrawField(Object target, FieldInfo field)
        {
            var dict = (IDictionary)field.GetValue(target);

            EditorGUILayout.LabelField(ObjectNames.NicifyVariableName(field.Name), EditorStyles.miniBoldLabel);
            if (dict == null)
            {
                EditorGUILayout.LabelField("(null)");
                return;
            }

            Type[] args = field.FieldType.GetGenericArguments();
            Type keyType = args[0];
            Type valueType = args[1];
            bool editable = Application.isPlaying;

            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(!editable))
            {
                object removeKey = null;
                object pendingKey = null;
                object pendingValue = null;

                foreach (DictionaryEntry entry in dict)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        object newKey = DrawGenericField(entry.Key, keyType);
                        object newValue = DrawGenericField(entry.Value, valueType);

                        if (!Equals(newKey, entry.Key))
                        {
                            removeKey = entry.Key;
                            pendingKey = newKey;
                            pendingValue = newValue;
                        }
                        else if (!Equals(newValue, entry.Value))
                        {
                            pendingKey = entry.Key;
                            pendingValue = newValue;
                        }

                        if (GUILayout.Button("x", GUILayout.Width(20)))
                        {
                            removeKey = entry.Key;
                            pendingKey = null;
                        }
                    }
                }

                if (removeKey != null)
                {
                    dict.Remove(removeKey);
                }

                if (pendingKey != null)
                {
                    dict[pendingKey] = pendingValue;
                }

                if (GUILayout.Button("+ Add Entry"))
                {
                    object newKey = CreateDefaultValue(keyType);
                    if (newKey != null && dict.Contains(newKey))
                    {
                        Debug.LogWarning("Can't add a new entry: the default key already exists. Edit that entry's key first, then add another.");
                    }
                    else
                    {
                        dict[newKey] = CreateDefaultValue(valueType);
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        private static object CreateDefaultValue(Type type)
        {
            if (type == typeof(string))
            {
                return string.Empty;
            }

            if (typeof(Object).IsAssignableFrom(type))
            {
                return null;
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        private static object DrawGenericField(object value, Type type)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                return EditorGUILayout.ObjectField((Object)value, type, true);
            }

            if (type == typeof(string))
            {
                return EditorGUILayout.TextField((string)value ?? string.Empty);
            }

            if (type == typeof(int))
            {
                return EditorGUILayout.IntField(value != null ? (int)value : 0);
            }

            if (type == typeof(long))
            {
                return EditorGUILayout.LongField(value != null ? (long)value : 0L);
            }

            if (type == typeof(float))
            {
                return EditorGUILayout.FloatField(value != null ? (float)value : 0f);
            }

            if (type == typeof(double))
            {
                return EditorGUILayout.DoubleField(value != null ? (double)value : 0d);
            }

            if (type == typeof(bool))
            {
                return EditorGUILayout.Toggle(value != null && (bool)value);
            }

            if (type.IsEnum)
            {
                return EditorGUILayout.EnumPopup((Enum)(value ?? Activator.CreateInstance(type)));
            }

            if (type == typeof(Vector2))
            {
                return EditorGUILayout.Vector2Field(GUIContent.none, value != null ? (Vector2)value : default);
            }

            if (type == typeof(Vector3))
            {
                return EditorGUILayout.Vector3Field(GUIContent.none, value != null ? (Vector3)value : default);
            }

            if (type == typeof(Color))
            {
                return EditorGUILayout.ColorField(value != null ? (Color)value : default);
            }

            EditorGUILayout.LabelField($"({type.Name} not editable inline)");
            return value;
        }
    }
}
