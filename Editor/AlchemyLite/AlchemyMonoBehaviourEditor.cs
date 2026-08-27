using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.AlchemyLite
{
    /// <summary>
    /// Generic fallback Inspector for every MonoBehaviour that doesn't already have a more specific
    /// custom editor (Unity picks the most specific one, so components with their own editor are
    /// unaffected). Renders the normal default inspector, then appends a live view/editor for any
    /// <see cref="AlchemyDictionaryAttribute"/> fields. See that attribute for usage.
    /// </summary>
    [CustomEditor(typeof(MonoBehaviour), true)]
    [CanEditMultipleObjects]
    public sealed class AlchemyMonoBehaviourEditor : Editor
    {
        private FieldInfo[] alchemyFields;

        private void OnEnable()
        {
            alchemyFields = AlchemyDictionaryFieldCache.GetFields(target.GetType());
            if (alchemyFields.Length > 0)
            {
                // Values can change from game code between frames (and OnEnable may not re-fire on
                // entering/exiting Play mode if domain reload is disabled) — keep repainting regardless.
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

            if (alchemyFields.Length == 0)
            {
                return;
            }

            if (targets.Length > 1)
            {
                EditorGUILayout.HelpBox("Select a single object to view/edit its Alchemy Dictionaries.", MessageType.None);
                return;
            }

            AlchemyDictionaryDrawerGUI.DrawAll(target, alchemyFields);
        }
    }
}
