using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.LiveDictionary
{
    /// <summary>
    /// Generic fallback Inspector for every ScriptableObject that doesn't already have a more specific
    /// custom editor. See <see cref="LiveDictionaryMonoBehaviourEditor"/> and <see cref="LiveDictionaryAttribute"/>.
    /// </summary>
    [CustomEditor(typeof(ScriptableObject), true)]
    [CanEditMultipleObjects]
    public sealed class LiveDictionaryScriptableObjectEditor : Editor
    {
        private FieldInfo[] liveDictionaryFields;

        private void OnEnable()
        {
            liveDictionaryFields = LiveDictionaryFieldCache.GetFields(target.GetType());
            if (liveDictionaryFields.Length > 0)
            {
                EditorApplication.update += Repaint;
            }
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (liveDictionaryFields.Length == 0)
            {
                return;
            }

            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox("Select a single object to view/edit its Live Dictionaries.", MessageType.None);
                return;
            }

            LiveDictionaryDrawerGUI.DrawAll(target, liveDictionaryFields);
        }
    }
}
