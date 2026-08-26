using System.Collections.Generic;
using UnityEditor;

namespace Thinhnv.UnityTools.SoCreator
{
    /// <summary>
    /// Project-wide configuration for SO Creator: which ScriptableObject types can be created
    /// through it, and under what menu path/file name. Stored at
    /// <c>ProjectSettings/SoCreatorSettings.asset</c> so it's shared with the team through
    /// version control, and edited via Project Settings &gt; Thinhnv Tools &gt; SO Creator
    /// (<see cref="SoCreatorSettingsProvider"/>).
    /// </summary>
    [FilePath("ProjectSettings/SoCreatorSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class SoCreatorSettings : ScriptableSingleton<SoCreatorSettings>
    {
        public List<SoCreatorEntry> Entries = new();

        public void SaveSettings()
        {
            Save(true);
        }
    }
}
