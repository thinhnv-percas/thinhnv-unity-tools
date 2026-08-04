using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>One (axis, magnitude) row read back out of a definition.</summary>
    public struct DefinitionVariantRead
    {
        public BuildAxis axis;
        public int magnitude;
        public GameObject model;
        public GameObject breakPieces;
    }

    /// <summary>What an existing <c>ObjectDefinitionSO</c> already points at.</summary>
    public class DefinitionRead
    {
        public string typeName = string.Empty;
        public GameObject baseModel;
        public GameObject baseBreakPieces;
        public List<DefinitionVariantRead> variants = new List<DefinitionVariantRead>();

        /// <summary>Highest magnitude the definition mentions, so the cache can grow enough rows.</summary>
        public int MaxMagnitude
        {
            get
            {
                int max = 1;
                foreach (DefinitionVariantRead variant in variants)
                {
                    max = Mathf.Max(max, variant.magnitude);
                }

                return max;
            }
        }
    }

    /// <summary>
    /// Reads an <c>ObjectDefinitionSO</c> back out, the mirror of <see cref="ObjectDefinitionWriter"/>.
    ///
    /// This is what lets the tool adopt work it did not create - definitions filled in by hand or by
    /// <c>ObjectCatalogBuilderWindow</c> - so their prefabs can be baked, selected and partially rebuilt
    /// without re-collecting every source by hand.
    /// </summary>
    public static class ObjectDefinitionReader
    {
        /// <summary>
        /// Pull the type, base model/shatter and every size variant out of <paramref name="definition"/>,
        /// taking the break prefab from the <paramref name="slot"/> the entry is configured for.
        /// Returns null when the asset is not readable as a definition.
        /// </summary>
        public static DefinitionRead Read(ScriptableObject definition, BreakTargetSlot slot)
        {
            if (definition == null)
            {
                return null;
            }

            var serialized = new SerializedObject(definition);
            SerializedProperty variants = serialized.FindProperty(SmashMarketBridge.PropVariants);
            if (variants == null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] '{definition.name}' has no " +
                                 $"'{SmashMarketBridge.PropVariants}' field; nothing read.", definition);
                return null;
            }

            string slotProperty = slot == BreakTargetSlot.TntShatter
                ? SmashMarketBridge.PropBreakTntShatter
                : SmashMarketBridge.PropBreakShatter;

            var read = new DefinitionRead
            {
                typeName = ReadTypeName(serialized),
                baseModel = Reference(serialized, SmashMarketBridge.PropBaseModel),
                baseBreakPieces = Reference(serialized,
                    $"{SmashMarketBridge.PropBaseBreakEffects}.{slotProperty}"),
            };

            for (int i = 0; i < variants.arraySize; i++)
            {
                SerializedProperty element = variants.GetArrayElementAtIndex(i);
                int axisValue = element
                    .FindPropertyRelative(SmashMarketBridge.PropVariantAxis).intValue;

                if (!BuildAxisExtensions.IsDefined(axisValue))
                {
                    Debug.LogWarning($"[ObjectDefBuilder] '{definition.name}' variant {i} has " +
                                     $"unknown axis value {axisValue}; skipped.", definition);
                    continue;
                }

                read.variants.Add(new DefinitionVariantRead
                {
                    axis = (BuildAxis)axisValue,
                    magnitude = element
                        .FindPropertyRelative(SmashMarketBridge.PropVariantMagnitude).intValue,
                    model = Relative(element, SmashMarketBridge.PropVariantModel),
                    breakPieces = Relative(element,
                        $"{SmashMarketBridge.PropVariantBreakEffects}.{slotProperty}"),
                });
            }

            return read;
        }

        private static string ReadTypeName(SerializedObject serialized)
        {
            SerializedProperty type = serialized.FindProperty(SmashMarketBridge.PropType);
            return type == null ? string.Empty : SmashMarketBridge.ObjectTypeName(type.intValue);
        }

        private static GameObject Reference(SerializedObject serialized, string path)
        {
            SerializedProperty property = serialized.FindProperty(path);
            return property == null ? null : property.objectReferenceValue as GameObject;
        }

        private static GameObject Relative(SerializedProperty element, string relativePath)
        {
            SerializedProperty property = element.FindPropertyRelative(relativePath);
            return property == null ? null : property.objectReferenceValue as GameObject;
        }
    }
}
