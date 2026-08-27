using System;
using System.Collections.Generic;
using UnityEngine;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Hidden backing store for every <see cref="AlchemySerializeFieldAttribute"/> Dictionary field on
    /// a class. One JSON blob per field (keyed by field name via two parallel lists, since Unity can't
    /// natively serialize a Dictionary either — including this one) plus a single shared list of
    /// <see cref="UnityEngine.Object"/> references so embedded object values don't need to be
    /// duplicated or re-serialized per entry.
    /// </summary>
    [Serializable]
    public sealed class AlchemyDictionaryStorage
    {
        [SerializeField] private List<string> fieldNames = new();
        [SerializeField] private List<string> fieldJson = new();
        [SerializeField] private List<UnityEngine.Object> objectReferences = new();

        internal List<UnityEngine.Object> ObjectReferences => objectReferences;

        internal void Clear()
        {
            fieldNames.Clear();
            fieldJson.Clear();
            objectReferences.Clear();
        }

        internal void Add(string fieldName, string json)
        {
            fieldNames.Add(fieldName);
            fieldJson.Add(json);
        }

        internal bool TryGetJson(string fieldName, out string json)
        {
            int index = fieldNames.IndexOf(fieldName);
            if (index < 0)
            {
                json = null;
                return false;
            }

            json = fieldJson[index];
            return true;
        }
    }
}
