using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Generic inspector for every <see cref="MonoBehaviour"/>: draws the default inspector,
/// then a button for each method decorated with <see cref="ContextMenuAttribute"/> — including
/// methods with parameters, whose arguments are drawn as fields (see <see cref="ContextMenuButtonGUI"/>).
/// Buttons run on every selected object, honouring <see cref="CanEditMultipleObjects"/>.
/// </summary>
[CanEditMultipleObjects]
[CustomEditor(typeof(MonoBehaviour), true)]
public class ContextMenuButtonEditor : Editor
{
    // Cached per-method argument values, persisted across inspector repaints.
    private readonly Dictionary<string, object[]> argCache = new Dictionary<string, object[]>();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        ContextMenuButtonGUI.Draw(targets, argCache);
    }
}

/// <summary>
/// Generic inspector for every <see cref="ScriptableObject"/>: draws the default inspector,
/// then a button for each <see cref="ContextMenuAttribute"/> method (parameterless or with
/// parameter fields). This lets any SO expose one-click editor actions (e.g. ObjectDefinitionSO's
/// "Apply Base Mass To Prefabs") without needing its own custom editor.
/// </summary>
[CanEditMultipleObjects]
[CustomEditor(typeof(ScriptableObject), true)]
public class ScriptableObjectContextMenuButtonEditor : Editor
{
    private readonly Dictionary<string, object[]> argCache = new Dictionary<string, object[]>();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        ContextMenuButtonGUI.Draw(targets, argCache);
    }
}
