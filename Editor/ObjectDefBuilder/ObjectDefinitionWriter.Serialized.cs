using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.ObjectDefBuilder
{
    /// <summary>
    /// The SerializedProperty plumbing behind <see cref="ObjectDefinitionWriter"/>. A missing field is
    /// reported instead of thrown so a game-side rename shows up as one warning rather than a broken build.
    /// </summary>
    public static partial class ObjectDefinitionWriter
    {
        /// <summary>Null every object-reference slot of a struct property (a freshly appended break group).</summary>
        private static void ClearBreakGroup(SerializedProperty group)
        {
            if (group == null)
            {
                return;
            }

            SerializedProperty iterator = group.Copy();
            SerializedProperty end = group.GetEndProperty();
            while (iterator.NextVisible(true) && !SerializedProperty.EqualContents(iterator, end))
            {
                if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                {
                    iterator.objectReferenceValue = null;
                }
            }
        }

        /// <summary>Assign an asset reference, leaving the field alone when there is nothing to write.</summary>
        private static void SetReference(SerializedObject serialized, string path, Object value)
        {
            if (value == null)
            {
                return;
            }

            SerializedProperty property = serialized.FindProperty(path);
            if (property == null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] ObjectDefinitionSO has no field '{path}'.");
                return;
            }

            property.objectReferenceValue = value;
        }

        private static void SetReference(SerializedProperty element, string relativePath, Object value)
        {
            if (value == null)
            {
                return;
            }

            SerializedProperty property = element.FindPropertyRelative(relativePath);
            if (property == null)
            {
                Debug.LogWarning($"[ObjectDefBuilder] ObjectVariant has no field '{relativePath}'.");
                return;
            }

            property.objectReferenceValue = value;
        }

        private static void SetInt(SerializedObject serialized, string path, int value)
        {
            SerializedProperty property = serialized.FindProperty(path);
            if (property != null)
            {
                // intValue (not enumValueIndex): ObjectType values are the raw level-JSON numbers.
                property.intValue = value;
            }
        }
    }
}
