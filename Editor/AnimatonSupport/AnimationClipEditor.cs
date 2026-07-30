using UnityEditor;
using UnityEngine;

public class AnimationClipEditor
{
    [MenuItem("CONTEXT/AnimationClip/Set Legacy")]
    private static void SetLegacy(MenuCommand command)
    {
        AnimationClip clip = (AnimationClip)command.context;
        SerializedObject serializedClip = new SerializedObject(clip);
        SerializedProperty legacyProp = serializedClip.FindProperty("m_Legacy");

        if (legacyProp != null)
        {
            legacyProp.boolValue = true;
            serializedClip.ApplyModifiedProperties();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            Debug.Log($"'{clip.name}' has been set to Legacy.");
        }
        else
        {
            Debug.LogWarning("Could not find the 'm_Legacy' property.");
        }
    }

    [MenuItem("CONTEXT/AnimationClip/Unset Legacy")]
    private static void UnsetLegacy(MenuCommand command)
    {
        AnimationClip clip = (AnimationClip)command.context;
        SerializedObject serializedClip = new SerializedObject(clip);
        SerializedProperty legacyProp = serializedClip.FindProperty("m_Legacy");

        if (legacyProp != null)
        {
            legacyProp.boolValue = false;
            serializedClip.ApplyModifiedProperties();
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            Debug.Log($"'{clip.name}' has been unset from Legacy.");
        }
        else
        {
            Debug.LogWarning("Could not find the 'm_Legacy' property.");
        }
    }
}