using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System;
using UnityEngine.SceneManagement;
using System.Linq;

public class AutoGridSnap : EditorWindow
{
    private Scene _currentSceneName;
    private int _selectedIndex;
    private List<Type> _typeListInScene;
    private string[] _typeListName;

    private Vector3 _prevPosition;
    private bool _doSnap = true;
    private bool _globalPos = false;
    private Vector3 _snapValue = new Vector3(0.5f, 0.5f, 0.5f);

    [MenuItem("Tools/Auto Grid Snap %_l")]

    static void Init()
    {
        var window = (AutoGridSnap)GetWindow(typeof(AutoGridSnap));
        window.maxSize = new Vector2(200, 125);
        window.minSize = new Vector2(200, 125);
    }

    public void OnGUI()
    {
        if (SceneManager.GetActiveScene() != _currentSceneName)
        {
            _currentSceneName = SceneManager.GetActiveScene();
            _typeListInScene = FindMonoBehavioursInSceneExcludeCanvas();
            _typeListInScene.Insert(0, typeof(Transform));

            _typeListName = _typeListInScene.Select(t => t.Name).ToArray();
        }

        _doSnap = EditorGUILayout.Toggle("Auto Snap", _doSnap);
        _globalPos = EditorGUILayout.Toggle("Global Position", _globalPos);

        EditorGUILayout.LabelField("Snap Type");

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(GUIContent.none, GUILayout.Width(27));
        _selectedIndex = EditorGUILayout.Popup(_selectedIndex, _typeListName);
        EditorGUILayout.EndHorizontal();

        _snapValue = EditorGUILayout.Vector3Field("Snap Value", _snapValue);
    }

    public void Update()
    {
        if (!_doSnap || EditorApplication.isPlaying || Selection.transforms.Length <= 0 ||
            Selection.transforms[0].position == _prevPosition) return;
        Snap();
        _prevPosition = Selection.transforms[0].position;
    }

    private void Snap()
    {
        foreach (var transform in Selection.transforms)
        {
            if ((_typeListInScene?.Count ?? 0) != 0 && transform.GetComponent(_typeListInScene[_selectedIndex]) == null) return;

            Vector3 t = transform.transform.position;
            if (!_globalPos) t = transform.transform.localPosition;

            t.x = Round(t.x, _snapValue.x);
            t.y = Round(t.y, _snapValue.y);
            t.z = Round(t.z, _snapValue.z);

            if (_globalPos) transform.transform.position = t;
            else transform.transform.localPosition = t;
        }
    }

    private float Round(float input, float snapValue)
    {
        return snapValue * Mathf.Round((input / snapValue));
    }

    private List<Type> FindMonoBehavioursInSceneExcludeCanvas()
    {
        MonoBehaviour[] allMonoBehaviours = GameObject.FindObjectsOfType<MonoBehaviour>(true);
        List<Type> uniqueTypes = new List<Type>();

        foreach (var mb in allMonoBehaviours)
        {
            if (mb == null || IsUnderCanvas(mb.gameObject))
                continue;

            uniqueTypes.Add(mb.GetType());
        }

        return uniqueTypes;
    }

    private static bool IsUnderCanvas(GameObject obj)
    {
        Transform current = obj.transform;
        while (current != null)
        {
            if (current.GetComponent<Canvas>() != null)
                return true;
            current = current.parent;
        }
        return false;
    }
}