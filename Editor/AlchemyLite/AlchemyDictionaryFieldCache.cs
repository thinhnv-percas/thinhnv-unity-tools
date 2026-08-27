using System;
using System.Collections.Generic;
using System.Reflection;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Per-type cache of <see cref="AlchemyDictionaryAttribute"/> fields, so the generic MonoBehaviour/
    /// ScriptableObject editors (which run for every object that doesn't have a more specific custom
    /// editor) only pay the reflection cost once per type instead of on every Inspector repaint.
    /// </summary>
    internal static class AlchemyDictionaryFieldCache
    {
        private const BindingFlags FieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly Dictionary<Type, FieldInfo[]> Cache = new();

        public static FieldInfo[] GetFields(Type type)
        {
            if (Cache.TryGetValue(type, out FieldInfo[] cached))
            {
                return cached;
            }

            var fields = new List<FieldInfo>();
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(FieldFlags))
                {
                    if (!field.IsDefined(typeof(AlchemyDictionaryAttribute), false))
                    {
                        continue;
                    }

                    if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        fields.Add(field);
                    }
                }
            }

            FieldInfo[] result = fields.ToArray();
            Cache[type] = result;
            return result;
        }
    }
}
