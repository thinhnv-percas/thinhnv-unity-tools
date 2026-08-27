using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Reflection-driven engine behind <see cref="AlchemySerializeFieldAttribute"/>: finds every marked
    /// Dictionary field on a target (walking its whole type hierarchy), and serializes/deserializes each
    /// one to/from a per-field JSON blob in an <see cref="AlchemyDictionaryStorage"/>.
    ///
    /// Values are round-tripped through <see cref="JsonUtility"/> via a tiny generic <c>Box&lt;T&gt;</c>
    /// wrapper (JsonUtility requires a class/struct root, not a bare primitive/string), except
    /// <see cref="UnityEngine.Object"/> keys/values — those can't be embedded in JSON, so they're
    /// substituted with an index into a shared reference list instead, mirroring how Alchemy's
    /// AlchemyJsonAdapter handles Object references.
    /// </summary>
    public static class AlchemyDictionarySerializer
    {
        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        [Serializable]
        private sealed class Box<T>
        {
            public T v;
        }

        [Serializable]
        private sealed class DictEntry
        {
            public string keyJson;
            public int keyRef = -1;
            public string valueJson;
            public int valueRef = -1;
        }

        [Serializable]
        private sealed class DictData
        {
            public List<DictEntry> entries = new();
        }

        /// <summary>Every field declared anywhere in <paramref name="type"/>'s hierarchy that carries <see cref="AlchemySerializeFieldAttribute"/>.</summary>
        public static IEnumerable<FieldInfo> GetAlchemyDictionaryFields(Type type)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(FieldFlags))
                {
                    if (field.IsDefined(typeof(AlchemySerializeFieldAttribute), false))
                    {
                        yield return field;
                    }
                }
            }
        }

        public static void OnBeforeSerialize(object target, AlchemyDictionaryStorage storage)
        {
            storage.Clear();
            foreach (FieldInfo field in GetAlchemyDictionaryFields(target.GetType()))
            {
                if (!IsSupportedDictionaryField(field))
                {
                    continue;
                }

                Type[] args = field.FieldType.GetGenericArguments();
                var dict = (IDictionary)(field.GetValue(target) ?? Activator.CreateInstance(field.FieldType));

                try
                {
                    string json = SerializeDictionary(dict, args[0], args[1], storage.ObjectReferences);
                    storage.Add(field.Name, json);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        public static void OnAfterDeserialize(object target, AlchemyDictionaryStorage storage)
        {
            foreach (FieldInfo field in GetAlchemyDictionaryFields(target.GetType()))
            {
                if (!IsSupportedDictionaryField(field) || !storage.TryGetJson(field.Name, out string json))
                {
                    continue;
                }

                try
                {
                    Type[] args = field.FieldType.GetGenericArguments();
                    IDictionary dict = DeserializeDictionary(json, field.FieldType, args[0], args[1], storage.ObjectReferences);
                    field.SetValue(target, dict);
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }
        }

        private static bool IsSupportedDictionaryField(FieldInfo field)
        {
            if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                Debug.LogWarning(
                    $"[AlchemyLite] '{field.DeclaringType?.Name}.{field.Name}' has [AlchemySerializeField] " +
                    "but isn't a Dictionary<,> field. This lightweight port only supports Dictionary fields; " +
                    "the value will not be persisted.");
                return false;
            }

            if (!field.IsDefined(typeof(NonSerializedAttribute), false))
            {
                Debug.LogWarning(
                    $"[AlchemyLite] '{field.DeclaringType?.Name}.{field.Name}' is missing [NonSerialized]. " +
                    "Add it alongside [AlchemySerializeField] so Unity doesn't also try to serialize it natively.");
            }

            return true;
        }

        private static string SerializeDictionary(IDictionary dict, Type keyType, Type valueType, List<Object> refs)
        {
            bool keyIsObject = typeof(Object).IsAssignableFrom(keyType);
            bool valueIsObject = typeof(Object).IsAssignableFrom(valueType);

            var data = new DictData();
            foreach (DictionaryEntry kv in dict)
            {
                var entry = new DictEntry();

                if (keyIsObject)
                {
                    entry.keyRef = AddOrFindRef(refs, (Object)kv.Key);
                }
                else
                {
                    entry.keyJson = BoxToJson(kv.Key, keyType);
                }

                if (valueIsObject)
                {
                    entry.valueRef = AddOrFindRef(refs, (Object)kv.Value);
                }
                else
                {
                    entry.valueJson = BoxToJson(kv.Value, valueType);
                }

                data.entries.Add(entry);
            }

            return JsonUtility.ToJson(data);
        }

        private static IDictionary DeserializeDictionary(
            string json, Type dictType, Type keyType, Type valueType, IReadOnlyList<Object> refs)
        {
            var dict = (IDictionary)Activator.CreateInstance(dictType);
            if (string.IsNullOrEmpty(json))
            {
                return dict;
            }

            DictData data = JsonUtility.FromJson<DictData>(json);
            if (data?.entries == null)
            {
                return dict;
            }

            bool keyIsObject = typeof(Object).IsAssignableFrom(keyType);
            bool valueIsObject = typeof(Object).IsAssignableFrom(valueType);

            foreach (DictEntry entry in data.entries)
            {
                object key = keyIsObject ? ResolveRef(refs, entry.keyRef) : BoxFromJson(entry.keyJson, keyType);
                if (key == null)
                {
                    continue; // Dictionary keys can't be null.
                }

                object value = valueIsObject ? ResolveRef(refs, entry.valueRef) : BoxFromJson(entry.valueJson, valueType);
                dict[key] = value;
            }

            return dict;
        }

        private static int AddOrFindRef(List<Object> refs, Object obj)
        {
            if (obj == null)
            {
                return -1;
            }

            int index = refs.IndexOf(obj);
            if (index < 0)
            {
                refs.Add(obj);
                index = refs.Count - 1;
            }

            return index;
        }

        private static Object ResolveRef(IReadOnlyList<Object> refs, int index)
        {
            return index >= 0 && index < refs.Count ? refs[index] : null;
        }

        private static string BoxToJson(object value, Type type)
        {
            Type boxType = typeof(Box<>).MakeGenericType(type);
            object box = Activator.CreateInstance(boxType);
            boxType.GetField(nameof(Box<object>.v)).SetValue(box, value);
            return JsonUtility.ToJson(box);
        }

        private static object BoxFromJson(string json, Type type)
        {
            Type boxType = typeof(Box<>).MakeGenericType(type);
            object box = string.IsNullOrEmpty(json) ? Activator.CreateInstance(boxType) : JsonUtility.FromJson(json, boxType);
            return boxType.GetField(nameof(Box<object>.v)).GetValue(box);
        }
    }
}
