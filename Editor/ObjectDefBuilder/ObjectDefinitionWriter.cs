using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>One (axis, magnitude) row to write into a definition's variants list.</summary>
    public struct VariantWrite
    {
        public BuildAxis axis;
        public int magnitude;
        public GameObject model;
        public GameObject breakPieces;
    }

    /// <summary>
    /// Everything a build wants to push into one <c>ObjectDefinitionSO</c>. A class, not a struct, so
    /// the per-size build steps can fill it in as they go.
    /// </summary>
    public class DefinitionWrite
    {
        public GameObject baseModel;
        public GameObject baseBreakPieces;
        public List<VariantWrite> variants = new List<VariantWrite>();
    }

    /// <summary>
    /// Writes build results into an <c>ObjectDefinitionSO</c> through <see cref="SerializedObject"/>
    /// (see <see cref="SmashMarketBridge"/> for why it is not typed).
    ///
    /// The write is a merge: only the model and the targeted break slot are touched, so explode / TNT /
    /// ground prefabs and sound names wired by hand survive a rebuild. Rows whose prefab came out null
    /// are skipped rather than clearing what is already there.
    /// </summary>
    public static partial class ObjectDefinitionWriter
    {
        /// <summary>Load the definition at <paramref name="folder"/>/Def_<paramref name="typeName"/>.asset, creating it if missing.</summary>
        public static ScriptableObject LoadOrCreate(string folder, string typeName)
        {
            if (SmashMarketBridge.ObjectDefinitionType == null)
            {
                return null;
            }

            ToolAssetUtil.EnsureFolder(folder);
            string path = $"{folder}/Def_{typeName}.asset";

            var existing = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (existing != null)
            {
                return existing;
            }

            ScriptableObject created = ScriptableObject.CreateInstance(SmashMarketBridge.ObjectDefinitionType);
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        /// <summary>Merge <paramref name="data"/> into <paramref name="definition"/>.</summary>
        public static void Write(ScriptableObject definition, int? objectTypeValue,
            DefinitionWrite data, BreakTargetSlot slot)
        {
            if (definition == null)
            {
                return;
            }

            string slotProperty = SlotProperty(slot);
            var serialized = new SerializedObject(definition);

            if (objectTypeValue.HasValue)
            {
                SetInt(serialized, SmashMarketBridge.PropType, objectTypeValue.Value);
            }

            SetReference(serialized, SmashMarketBridge.PropBaseModel, data.baseModel);
            SetReference(serialized,
                $"{SmashMarketBridge.PropBaseBreakEffects}.{slotProperty}", data.baseBreakPieces);

            if (data.variants != null)
            {
                WriteVariants(serialized, data.variants, slotProperty);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(definition);
        }

        private static void WriteVariants(SerializedObject serialized, List<VariantWrite> writes, string slotProperty)
        {
            SerializedProperty variants = serialized.FindProperty(SmashMarketBridge.PropVariants);
            if (variants == null)
            {
                Debug.LogError($"[ObjectDefBuilder] ObjectDefinitionSO has no " +
                               $"'{SmashMarketBridge.PropVariants}' field; size variants not written.");
                return;
            }

            foreach (VariantWrite write in writes)
            {
                SerializedProperty element = FindOrAppend(variants, write.axis, write.magnitude);
                SetReference(element, SmashMarketBridge.PropVariantModel, write.model);
                SetReference(element,
                    $"{SmashMarketBridge.PropVariantBreakEffects}.{slotProperty}", write.breakPieces);
            }
        }

        /// <summary>The element for an (axis, magnitude), appending a cleared one when it does not exist yet.</summary>
        private static SerializedProperty FindOrAppend(SerializedProperty variants, BuildAxis axis, int magnitude)
        {
            int axisValue = SmashMarketBridge.SizeAxisValue(axis);

            for (int i = 0; i < variants.arraySize; i++)
            {
                SerializedProperty element = variants.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative(SmashMarketBridge.PropVariantAxis).intValue == axisValue &&
                    element.FindPropertyRelative(SmashMarketBridge.PropVariantMagnitude).intValue == magnitude)
                {
                    return element;
                }
            }

            int index = variants.arraySize;
            variants.InsertArrayElementAtIndex(index);
            SerializedProperty added = variants.GetArrayElementAtIndex(index);

            // InsertArrayElementAtIndex copies the previous element - clear it before filling in.
            added.FindPropertyRelative(SmashMarketBridge.PropVariantAxis).intValue = axisValue;
            added.FindPropertyRelative(SmashMarketBridge.PropVariantMagnitude).intValue = magnitude;
            added.FindPropertyRelative(SmashMarketBridge.PropVariantModel).objectReferenceValue = null;
            ClearBreakGroup(added.FindPropertyRelative(SmashMarketBridge.PropVariantBreakEffects));
            return added;
        }

        private static string SlotProperty(BreakTargetSlot slot) => slot == BreakTargetSlot.TntShatter
            ? SmashMarketBridge.PropBreakTntShatter
            : SmashMarketBridge.PropBreakShatter;
    }
}
