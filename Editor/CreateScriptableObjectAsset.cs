using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ThinhnvTools
{
    public static class CreateScriptableObjectAsset
    {
        #region Private Methods

        [MenuItem("Assets/Create/ScriptableObject Asset From Script", false, 20)]
        private static void CreateFromProjectWindow()
        {
            CreateAssetFromScript(Selection.activeObject as MonoScript);
        }

        [MenuItem("Assets/Create/ScriptableObject Asset From Script", true)]
        private static bool ValidateCreateFromProjectWindow()
        {
            return IsCreatableScriptableObject(Selection.activeObject as MonoScript);
        }

        [MenuItem("CONTEXT/MonoImporter/Create ScriptableObject Asset")]
        private static void CreateFromInspectorContext(MenuCommand command)
        {
            CreateAssetFromScript(GetScriptFromImporter(command.context as MonoImporter));
        }

        [MenuItem("CONTEXT/MonoImporter/Create ScriptableObject Asset", true)]
        private static bool ValidateCreateFromInspectorContext(MenuCommand command)
        {
            return IsCreatableScriptableObject(GetScriptFromImporter(command.context as MonoImporter));
        }

        private static MonoScript GetScriptFromImporter(MonoImporter importer)
        {
            return importer != null ? AssetDatabase.LoadAssetAtPath<MonoScript>(importer.assetPath) : null;
        }

        private static bool IsCreatableScriptableObject(MonoScript script)
        {
            if (script == null)
            {
                return false;
            }

            Type type = script.GetClass();
            return type != null
                && typeof(ScriptableObject).IsAssignableFrom(type)
                && !type.IsAbstract
                && !type.IsGenericTypeDefinition;
        }

        private static void CreateAssetFromScript(MonoScript script)
        {
            if (!IsCreatableScriptableObject(script))
            {
                Debug.LogWarning($"'{script?.name}' does not define a creatable ScriptableObject type.");
                return;
            }

            Type type = script.GetClass();
            ScriptableObject instance = ScriptableObject.CreateInstance(type);

            string scriptFolder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(script));
            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{scriptFolder}/{type.Name}.asset");

            AssetDatabase.CreateAsset(instance, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = instance;
            EditorGUIUtility.PingObject(instance);

            Debug.Log($"Created ScriptableObject asset '{assetPath}'.");
        }

        #endregion
    }
}
