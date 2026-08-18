#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;
using System.Reflection;
using System.Linq;
using System.IO;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class SceneSwitcherToolbar
{
    private const string ToolbarId = "SceneSwitcher/Scene Switcher";

    private static string[] sceneNames = new string[0];
    private static int selectedIndex = 0;
    private static string lastActiveScene = "";

    private static float dropdownBoxHeight = 20f; // Dropdown button height

    private static bool fetchAllScenes
    {
        get => EditorPrefs.GetBool("SceneSwitcher_FetchAllScenes", false);
        set => EditorPrefs.SetBool("SceneSwitcher_FetchAllScenes", value);
    }

    static SceneSwitcherToolbar()
    {
        RefreshSceneList();
        SelectCurrentScene(); // Automatically select the open scene

        // Hook into scene change events
        EditorSceneManager.activeSceneChangedInEditMode += (prev, current) => UpdateSceneSelection();
        EditorApplication.playModeStateChanged += OnPlayModeChanged;

        // The attribute-driven element can be stale right after a domain reload;
        // nudge Unity to (re)build it once the editor has settled.
        EditorApplication.delayCall += () => MainToolbar.Refresh(ToolbarId);
    }

    [MainToolbarElement(
        ToolbarId,
        defaultDockPosition = MainToolbarDockPosition.Left)]
    public static MainToolbarElement CreateToolbarElement()
    {
        // MainToolbarCustom is the only MainToolbarElement that can host an arbitrary
        // VisualElement. Its base type/ctor/members are public, but Unity keeps the
        // class itself internal, so it must be constructed via reflection.
        Type customType = typeof(MainToolbarButton)
            .Assembly
            .GetType(
                "UnityEditor.Toolbars.MainToolbarCustom",
                throwOnError: true);

        return (MainToolbarElement)Activator.CreateInstance(
            customType,
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic,
            binder: null,
            args: new object[]
            {
                (Func<VisualElement>)BuildElement
            },
            culture: null);
    }

    private static VisualElement BuildElement()
    {
        var container = new IMGUIContainer(OnGUI);
        container.style.marginLeft = 4;
        container.style.marginRight = 4;
        return container;
    }

    static void OnGUI()
    {
        CheckAndRefreshScenes();

        if (selectedIndex >= sceneNames.Length)
            selectedIndex = 0;

        bool isPlaying = EditorApplication.isPlaying; // Check if in Play Mode

        GUILayout.BeginHorizontal();

        // Fetch all scenes toggle button (Disabled in Play Mode)
        EditorGUI.BeginDisabledGroup(isPlaying);
        bool newFetchAllScenes = GUILayout.Toggle(fetchAllScenes, "All Scenes", "Button", GUILayout.Height(dropdownBoxHeight));
        if (newFetchAllScenes != fetchAllScenes)
        {
            fetchAllScenes = newFetchAllScenes;
            RefreshSceneList();
            SelectCurrentScene();
        }
        EditorGUI.EndDisabledGroup();

        // Scene dropdown with the currently selected scene displayed (Disabled in Play Mode)
        EditorGUI.BeginDisabledGroup(isPlaying);
        GUIStyle popupStyle = new GUIStyle(EditorStyles.popup)
        {
            fixedHeight = dropdownBoxHeight
        };

        int newIndex = EditorGUILayout.Popup(selectedIndex, sceneNames, popupStyle, GUILayout.Width(150), GUILayout.Height(dropdownBoxHeight));

        if (newIndex != selectedIndex)
        {
            selectedIndex = newIndex;
            LoadScene(sceneNames[selectedIndex]);
        }
        EditorGUI.EndDisabledGroup();

        GUILayout.EndHorizontal();
    }

    static void RefreshSceneList()
    {
        if (fetchAllScenes)
        {
            sceneNames = Directory.GetFiles("Assets", "*.unity", SearchOption.AllDirectories)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .ToArray();
        }
        else
        {
            sceneNames = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => Path.GetFileNameWithoutExtension(scene.path))
                .ToArray();

            var curentScene = SceneManager.GetActiveScene().name;

            if (!sceneNames.Contains(curentScene))
            {
                sceneNames = InsertAtStart(sceneNames, curentScene);
            }
        }
    }

    public static T[] InsertAtStart<T>(T[] array, T element)
    {
        T[] newArray = new T[array.Length + 1];
        newArray[0] = element;
        array.CopyTo(newArray, 1);
        return newArray;
    }

    static void CheckAndRefreshScenes()
    {
        string[] currentScenes;
        if (fetchAllScenes)
        {
            currentScenes = Directory.GetFiles("Assets", "*.unity", SearchOption.AllDirectories)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .ToArray();
        }
        else
        {
            currentScenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => Path.GetFileNameWithoutExtension(scene.path))
                .ToArray();

            var curentScene = SceneManager.GetActiveScene().name;


            if (!currentScenes.Contains(curentScene))
            {
                currentScenes = InsertAtStart(currentScenes, curentScene);
            }
        }

        if (!currentScenes.SequenceEqual(sceneNames))
        {
            sceneNames = currentScenes;
            SelectCurrentScene();
        }
    }

    static void SelectCurrentScene()
    {
        string currentScene = Path.GetFileNameWithoutExtension(EditorSceneManager.GetActiveScene().path);
        int index = System.Array.IndexOf(sceneNames, currentScene);
        if (index != -1)
        {
            selectedIndex = index;
            lastActiveScene = currentScene;
        }
    }

    static void UpdateSceneSelection()
    {
        string currentScene = Path.GetFileNameWithoutExtension(EditorSceneManager.GetActiveScene().path);
        if (currentScene != lastActiveScene)
        {
            lastActiveScene = currentScene;
            SelectCurrentScene();
        }
    }

    static void LoadScene(string sceneName)
    {
        string scenePath;
        //Debug.Log("<b><color=green>Thank you for using the package  -Ajay</color></b>");

        if (fetchAllScenes)
        {
            scenePath = Directory.GetFiles("Assets", "*.unity", SearchOption.AllDirectories)
                .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path) == sceneName);
        }
        else
        {
            scenePath = EditorBuildSettings.scenes
                .FirstOrDefault(scene => scene.enabled && scene.path.Contains(sceneName))?.path;
        }

        if (!string.IsNullOrEmpty(scenePath))
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                EditorSceneManager.OpenScene(scenePath);
            }
        }
        else
        {
            Debug.LogError("Scene not found: " + sceneName);
        }
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.ExitingPlayMode)
        {
            EditorApplication.delayCall += () => MainToolbar.Refresh(ToolbarId);
        }
    }
}
#endif