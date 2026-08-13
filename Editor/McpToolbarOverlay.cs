#if UNITY_EDITOR

using System;
using System.Reflection;
using System.Threading.Tasks;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using MCPForUnity.Editor.Services.Transport;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class McpToolbarOverlay
{
    private const string ToolbarId = "MCP/MCP Server";

    private const double PollIntervalSeconds = 0.75d;

    private const double ServerLaunchHardCapSeconds = 300d;

    static McpToolbarOverlay()
    {
        // The attribute-driven element can be stale right after a domain reload;
        // nudge Unity to (re)build it once the editor has settled.
        EditorApplication.delayCall += () => MainToolbar.Refresh(ToolbarId);
    }

    [MainToolbarElement(
        ToolbarId,
        defaultDockPosition = MainToolbarDockPosition.Right)]
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
        bool serverOperationInProgress = false;
        bool sessionOperationInProgress = false;
        bool sessionRunning = false;
        bool serverReachable = false;
        double lastPollTime = -1d;

        VisualElement root = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center,
                marginLeft = 4,
                marginRight = 4,
            }
        };

        VisualElement statusDot = new VisualElement
        {
            style =
            {
                width = 8,
                height = 8,
                marginRight = 4,
                borderTopLeftRadius = 4,
                borderTopRightRadius = 4,
                borderBottomLeftRadius = 4,
                borderBottomRightRadius = 4,
                backgroundColor = new Color(0.5f, 0.5f, 0.5f),
            }
        };

        Label statusLabel = new Label("MCP status unknown")
        {
            style = { marginRight = 6 }
        };

        Button serverButton = null;
        Button connectButton = null;

        bool OperationInProgress() => serverOperationInProgress || sessionOperationInProgress;

        void RefreshStatus()
        {
            bool useHttpTransport = false;

            try
            {
                sessionRunning = MCPServiceLocator.Bridge.IsRunning;
                useHttpTransport = EditorConfigurationCache.Instance.UseHttpTransport;
                serverReachable = useHttpTransport && MCPServiceLocator.Server.IsLocalHttpServerReachable();

                TransportMode activeMode =
                    MCPServiceLocator.Bridge.ActiveMode ?? TransportMode.Http;

                int currentPort = MCPServiceLocator.Bridge.CurrentPort;

                string host = activeMode == TransportMode.Http
                    ? HttpEndpointUtility.GetBaseUrl()
                    : $"stdio:{currentPort}";

                statusDot.style.backgroundColor = sessionRunning
                    ? new Color(0.3f, 0.8f, 0.3f)
                    : new Color(0.85f, 0.3f, 0.3f);

                statusLabel.text = sessionRunning
                    ? $"MCP Session Active ({host})"
                    : $"MCP No Session ({host})";
            }
            catch (Exception ex)
            {
                statusLabel.text = $"MCP status error: {ex.Message}";
                sessionRunning = false;
                serverReachable = false;
            }

            if (!OperationInProgress())
            {
                serverButton.text = serverReachable ? "Stop Server" : "Start Server";
                serverButton.SetEnabled(useHttpTransport);
                serverButton.tooltip = useHttpTransport
                    ? string.Empty
                    : "Only applicable when transport is set to HTTP (see Window > MCP For Unity).";

                connectButton.text = sessionRunning ? "Disconnect" : "Connect";
                connectButton.SetEnabled(true);
            }
        }

        async void OnServerButtonClicked()
        {
            if (OperationInProgress())
                return;

            serverOperationInProgress = true;
            serverButton.SetEnabled(false);
            connectButton.SetEnabled(false);
            serverButton.text = serverReachable ? "Stopping..." : "Starting...";

            try
            {
                if (serverReachable)
                {
                    if (MCPServiceLocator.Bridge.IsRunning)
                    {
                        await MCPServiceLocator.Bridge.StopAsync();
                    }

                    MCPServiceLocator.Server.StopLocalHttpServer();
                }
                else
                {
                    if (!MCPServiceLocator.Server.CanStartLocalServer())
                    {
                        EditorUtility.DisplayDialog(
                            "Cannot Start HTTP Server",
                            "HTTP transport is disabled or the configured URL is not allowed to launch a local server. " +
                            "Check Window > MCP For Unity.",
                            "OK");
                        return;
                    }

                    bool started = MCPServiceLocator.Server.StartLocalHttpServer();
                    if (started)
                    {
                        await WaitForLocalServerThenConnectAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                serverOperationInProgress = false;
                RefreshStatus();
            }
        }

        async Task WaitForLocalServerThenConnectAsync()
        {
            var server = MCPServiceLocator.Server;
            var bridge = MCPServiceLocator.Bridge;
            double startTime = EditorApplication.timeSinceStartup;

            while (true)
            {
                if (server.IsLocalHttpServerReachable())
                {
                    await bridge.StartAsync();
                    return;
                }

                bool processAlive = server.IsManagedServerLaunchProcessAlive();
                double elapsed = EditorApplication.timeSinceStartup - startTime;

                if ((!processAlive && elapsed > 1.0) || elapsed > ServerLaunchHardCapSeconds)
                {
                    if (await bridge.StartAsync())
                    {
                        return;
                    }

                    server.LogLocalHttpServerLaunchFailure();
                    return;
                }

                await Task.Delay(500);
            }
        }

        async void OnConnectClicked()
        {
            if (OperationInProgress())
                return;

            sessionOperationInProgress = true;
            connectButton.SetEnabled(false);
            connectButton.text = sessionRunning ? "Disconnecting..." : "Connecting...";

            try
            {
                if (MCPServiceLocator.Bridge.IsRunning)
                {
                    await MCPServiceLocator.Bridge.StopAsync();
                }
                else
                {
                    await MCPServiceLocator.Bridge.StartAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                sessionOperationInProgress = false;
                RefreshStatus();
            }
        }

        serverButton = new Button(OnServerButtonClicked) { text = "...", style = { marginRight = 4 } };
        connectButton = new Button(OnConnectClicked) { text = "..." };

        root.Add(statusDot);
        root.Add(statusLabel);
        root.Add(serverButton);
        root.Add(connectButton);

        void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastPollTime < PollIntervalSeconds)
                return;

            lastPollTime = now;
            RefreshStatus();
        }

        // Poll on EditorApplication.update (not IMGUI repaint) so the status/session
        // toggle keeps refreshing even while no editor window is redrawing.
        root.RegisterCallback<AttachToPanelEvent>(_ => EditorApplication.update += OnEditorUpdate);
        root.RegisterCallback<DetachFromPanelEvent>(_ => EditorApplication.update -= OnEditorUpdate);

        RefreshStatus();

        return root;
    }
}

#endif
