using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Draws an editable key/value list for every <see cref="AlchemySerializeFieldAttribute"/>
    /// Dictionary field on a target — the fields are <c>[NonSerialized]</c> so they never show up
    /// through <c>SerializedProperty</c>/<c>DrawDefaultInspector</c>, and are instead edited directly
    /// via reflection on the live object. Shared by <see cref="AlchemyBehaviourEditor"/> and
    /// <see cref="AlchemyScriptableObjectEditor"/>.
    /// </summary>
    internal static class AlchemyDictionaryInspectorGUI
    {
        public static void DrawAll(Object target)
        {
            bool drewHeader = false;

            foreach (FieldInfo field in AlchemyDictionarySerializer.GetAlchemyDictionaryFields(target.GetType()))
            {
                if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                {
                    continue;
                }

                if (!drewHeader)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Alchemy Dictionaries", EditorStyles.boldLabel);
                    drewHeader = true;
                }

                DrawField(target, field);
            }
        }

        private static void DrawField(Object target, FieldInfo field)
        {
            var dict = (IDictionary)(field.GetValue(target) ?? Activator.CreateInstance(field.FieldType));
            field.SetValue(target, dict);

            Type[] args = field.FieldType.GetGenericArguments();
            Type keyType = args[0];
            Type valueType = args[1];

            EditorGUILayout.LabelField(ObjectNames.NicifyVariableName(field.Name), EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;

            bool changed = false;
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
                        changed = true;
                        removeKey = entry.Key;
                        pendingKey = newKey;
                        pendingValue = newValue;
                    }
                    else if (!Equals(newValue, entry.Value))
                    {
                        changed = true;
                        pendingKey = entry.Key;
                        pendingValue = newValue;
                    }

                    if (GUILayout.Button("x", GUILayout.Width(20)))
                    {
                        changed = true;
                        removeKey = entry.Key;
                        pendingKey = null;
                    }
                }
            }

            if (changed)
            {
                Undo.RecordObject(target, "Edit Alchemy Dictionary");
                if (removeKey != null)
                {
                    dict.Remove(removeKey);
                }

                if (pendingKey != null)
                {
                    dict[pendingKey] = pendingValue;
                }

                EditorUtility.SetDirty(target);
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
                    Undo.RecordObject(target, "Add Alchemy Dictionary Entry");
                    dict[newKey] = CreateDefaultValue(valueType);
                    EditorUtility.SetDirty(target);
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
