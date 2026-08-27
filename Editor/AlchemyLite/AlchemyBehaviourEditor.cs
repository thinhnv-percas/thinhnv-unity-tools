using UnityEditor;

namespace Thinhnv.UnityTools.AlchemyLite
{
    [CustomEditor(typeof(AlchemyBehaviour), true)]
    public sealed class AlchemyBehaviourEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            AlchemyDictionaryInspectorGUI.DrawAll(target);
        }
    }
}
