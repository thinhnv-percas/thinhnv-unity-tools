using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.SoCreator
{
    /// <summary>
    /// Finds ScriptableObject types defined in this project (scripts and installed packages),
    /// excluding types built into the Unity Editor itself. Used to populate/refresh
    /// <see cref="SoCreatorSettings"/> from Project Settings &gt; Thinhnv Tools &gt; SO Creator.
    /// </summary>
    public static class SoCreatorTypeScanner
    {
        public static List<Type> ScanProjectScriptableObjectTypes()
        {
            var result = new List<Type>();

            foreach (Type type in TypeCache.GetTypesDerivedFrom<ScriptableObject>())
            {
                if (type.IsAbstract || type.ContainsGenericParameters)
                {
                    continue;
                }

                if (!IsDefinedInProject(type))
                {
                    continue;
                }

                result.Add(type);
            }

            result.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
            return result;
        }

        private static bool IsDefinedInProject(Type type)
        {
            string location = type.Assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                return false;
            }

            string editorContentsPath = EditorApplication.applicationContentsPath.Replace('\\', '/');
            return !location.Replace('\\', '/').StartsWith(editorContentsPath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
