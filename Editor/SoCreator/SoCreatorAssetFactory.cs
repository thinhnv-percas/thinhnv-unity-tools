using System;
using UnityEditor;
using UnityEngine;

namespace Thinhnv.UnityTools.SoCreator
{
    /// <summary>
    /// Creates a ScriptableObject asset for a configured <see cref="SoCreatorEntry"/> in the
    /// currently selected project folder, using the same "type the name, hit enter" flow as
    /// Unity's native Assets &gt; Create menu.
    /// </summary>
    public static class SoCreatorAssetFactory
    {
        public static void CreateAssetInteractive(SoCreatorEntry entry)
        {
            Type type = entry?.ResolveType();
            if (type == null)
            {
                Debug.LogWarning(
                    $"SO Creator: could not resolve type '{entry?.AssemblyQualifiedTypeName}'. " +
                    "The script may have been renamed or removed — rescan from Project Settings > Thinhnv Tools > SO Creator.");
                return;
            }

            var instance = ScriptableObject.CreateInstance(type);
            string fileName = string.IsNullOrWhiteSpace(entry.FileName) ? type.Name : entry.FileName;
            ProjectWindowUtil.CreateAsset(instance, $"{fileName}.asset");
        }
    }
}
