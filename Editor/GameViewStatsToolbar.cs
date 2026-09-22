#if UNITY_EDITOR

using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class GameViewStatsToolbar
{
    private const string ToolbarId = "Game View/Statistics";
    private const double PollIntervalSeconds = 0.25d;

    private static Type s_GameViewType;
    private static FieldInfo s_StatsField;

    static GameViewStatsToolbar()
    {
        CacheGameViewMembers();

        EditorApplication.delayCall += () =>
        {
            MainToolbar.Refresh(ToolbarId);
        };
    }

    [MainToolbarElement(
        ToolbarId,
        defaultDockPosition = MainToolbarDockPosition.Right)]
    public static MainToolbarElement CreateToolbarElement()
    {
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
        var root = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center,
                marginLeft = 2,
                marginRight = 2,
            }
        };

        var toggle = new Toggle
        {
            text = "Stats",
            tooltip = "Show Game View Statistics"
        };

        toggle.style.marginLeft = 2;
        toggle.style.marginRight = 2;

        bool updating = false;
        double lastPollTime = -1d;

        void RefreshState()
        {
            if (updating)
                return;

            bool statsEnabled = GetStatsEnabled();

            if (toggle.value == statsEnabled)
                return;

            updating = true;

            try
            {
                toggle.SetValueWithoutNotify(statsEnabled);
            }
            finally
            {
                updating = false;
            }
        }

        toggle.RegisterValueChangedCallback(evt =>
        {
            if (updating)
                return;

            SetStatsEnabled(evt.newValue);

            // Game View repaint/state update.
            EditorApplication.delayCall += RefreshState;
        });

        root.Add(toggle);

        void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;

            if (now - lastPollTime < PollIntervalSeconds)
                return;

            lastPollTime = now;

            RefreshState();
        }

        root.RegisterCallback<AttachToPanelEvent>(_ =>
        {
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            RefreshState();
        });

        root.RegisterCallback<DetachFromPanelEvent>(_ =>
        {
            EditorApplication.update -= OnEditorUpdate;
        });

        RefreshState();

        return root;
    }

    private static void CacheGameViewMembers()
    {
        try
        {
            Assembly editorAssembly = typeof(EditorWindow).Assembly;

            s_GameViewType = editorAssembly.GetType(
                "UnityEditor.GameView");

            if (s_GameViewType == null)
            {
                Debug.LogWarning(
                    "[GameViewStatsToolbar] UnityEditor.GameView not found.");

                return;
            }

            s_StatsField = s_GameViewType.GetField(
                "m_Stats",
                BindingFlags.Instance |
                BindingFlags.NonPublic);

            if (s_StatsField == null)
            {
                Debug.LogWarning(
                    "[GameViewStatsToolbar] GameView.m_Stats not found.");

                return;
            }

            if (s_StatsField.FieldType != typeof(bool))
            {
                Debug.LogWarning(
                    "[GameViewStatsToolbar] GameView.m_Stats is not bool.");

                s_StatsField = null;
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    private static EditorWindow GetGameView()
    {
        if (s_GameViewType == null ||
            s_StatsField == null)
        {
            CacheGameViewMembers();
        }

        if (s_GameViewType == null)
            return null;

        EditorWindow[] windows =
            Resources.FindObjectsOfTypeAll<EditorWindow>();

        foreach (EditorWindow window in windows)
        {
            if (window != null &&
                window.GetType() == s_GameViewType)
            {
                return window;
            }
        }

        return null;
    }

    private static bool GetStatsEnabled()
    {
        try
        {
            EditorWindow gameView = GetGameView();

            if (gameView == null ||
                s_StatsField == null)
            {
                return false;
            }

            return (bool)s_StatsField.GetValue(gameView);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            return false;
        }
    }

    private static void SetStatsEnabled(bool enabled)
    {
        try
        {
            EditorWindow gameView = GetGameView();

            if (gameView == null)
            {
                Debug.LogWarning(
                    "[GameViewStatsToolbar] Game View is not open.");

                return;
            }

            if (s_StatsField == null)
            {
                Debug.LogWarning(
                    "[GameViewStatsToolbar] GameView.m_Stats not found.");

                return;
            }

            s_StatsField.SetValue(
                gameView,
                enabled);

            gameView.Repaint();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
}

#endif