using System;

namespace Thinhnv.UnityTools.SoCreator
{
    /// <summary>
    /// One ScriptableObject type registered with SO Creator: whether it shows up in the
    /// Assets &gt; Create &gt; SO Creator menu, and under what path/file name. Configured from
    /// Project Settings &gt; Thinhnv Tools &gt; SO Creator — see <see cref="SoCreatorSettings"/>.
    /// </summary>
    [Serializable]
    public class SoCreatorEntry
    {
        public string AssemblyQualifiedTypeName = "";
        public bool Enabled = true;
        public string MenuPath = "";
        public string FileName = "New Asset";

        public string DisplayTypeName
        {
            get
            {
                Type type = ResolveType();
                return type != null ? type.FullName : $"{AssemblyQualifiedTypeName} (missing)";
            }
        }

        public Type ResolveType()
        {
            return Type.GetType(AssemblyQualifiedTypeName);
        }
    }
}
