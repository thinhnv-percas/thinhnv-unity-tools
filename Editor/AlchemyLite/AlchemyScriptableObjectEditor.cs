using UnityEditor;

namespace Thinhnv.UnityTools.AlchemyLite
{
    [CustomEditor(typeof(AlchemyScriptableObject), true)]
    public sealed class AlchemyScriptableObjectEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            AlchemyDictionaryInspectorGUI.DrawAll(target);
        }
    }
}
