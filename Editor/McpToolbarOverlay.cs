#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
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

    // Cheap state check.
    private const double SessionPollIntervalSeconds = 1.0d;

    // Server TCP probe is asynchronous, so polling can be less frequent.
    private const double ServerPollIntervalSeconds = 5.0d;

    // TCP connection timeout.
    private const int ServerProbeTimeoutMs = 250;

    // Server startup timeout.
    private const double ServerLaunchHardCapSeconds = 300d;

    // Poll interval while waiting for the local server to start.
    private const int ServerLaunchPollIntervalMs = 500;

    private static readonly Color ConnectedColor =
        new Color(0.30f, 0.80f, 0.30f);

    private static readonly Color DisconnectedColor =
        new Color(0.85f, 0.30f, 0.30f);

    private static readonly Color UnknownColor =
        new Color(0.50f, 0.50f, 0.50f);

    static McpToolbarOverlay()
    {
        EditorApplication.delayCall += RefreshToolbar;
    }

    private static void RefreshToolbar()
    {
        try
        {
            MainToolbar.Refresh(ToolbarId);
        }
        catch
        {
            // Unity may not have created MainToolbar yet.
        }
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
        bool serverOperationInProgress = false;
        bool sessionOperationInProgress = false;

        bool sessionRunning = false;
        bool serverReachable = false;

        // IMPORTANT:
        // This must be refreshed from EditorConfigurationCache.
        bool useHttpTransport = false;

        double lastSessionPollTime = -1d;
        double lastServerPollTime = -1d;

        bool sessionStateKnown = false;
        bool serverStateKnown = false;
        bool transportStateKnown = false;

        bool serverProbeRunning = false;

        // Invalidates stale async probe results.
        int serverProbeGeneration = 0;

        CancellationTokenSource lifetimeCts =
            new CancellationTokenSource();

        VisualElement root = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center,
                marginLeft = 4,
                marginRight = 4
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

                backgroundColor = UnknownColor
            }
        };

        Label statusLabel = new Label("MCP status unknown")
        {
            style =
            {
                marginRight = 6
            }
        };

        Button serverButton = null;
        Button connectButton = null;

        string lastStatusText = null;
        string lastServerButtonText = null;
        string lastConnectButtonText = null;
        string lastServerTooltip = null;

        bool? lastServerButtonEnabled = null;
        bool? lastConnectButtonEnabled = null;

        Color? lastStatusColor = null;

        // ------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------

        bool OperationInProgress()
        {
            return serverOperationInProgress ||
                   sessionOperationInProgress;
        }

        void SetStatusText(string value)
        {
            if (string.Equals(
                    lastStatusText,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastStatusText = value;
            statusLabel.text = value;
        }

        void SetServerButtonText(string value)
        {
            if (string.Equals(
                    lastServerButtonText,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastServerButtonText = value;
            serverButton.text = value;
        }

        void SetConnectButtonText(string value)
        {
            if (string.Equals(
                    lastConnectButtonText,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastConnectButtonText = value;
            connectButton.text = value;
        }

        void SetServerTooltip(string value)
        {
            if (string.Equals(
                    lastServerTooltip,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            lastServerTooltip = value;
            serverButton.tooltip = value;
        }

        void SetServerButtonEnabled(bool value)
        {
            if (lastServerButtonEnabled == value)
                return;

            lastServerButtonEnabled = value;
            serverButton.SetEnabled(value);
        }

        void SetConnectButtonEnabled(bool value)
        {
            if (lastConnectButtonEnabled == value)
                return;

            lastConnectButtonEnabled = value;
            connectButton.SetEnabled(value);
        }

        void SetStatusColor(Color color)
        {
            if (lastStatusColor.HasValue &&
                lastStatusColor.Value == color)
            {
                return;
            }

            lastStatusColor = color;
            statusDot.style.backgroundColor = color;
        }

        // ------------------------------------------------------------
        // Transport configuration
        // ------------------------------------------------------------

        bool RefreshTransportState()
        {
            try
            {
                bool newUseHttpTransport =
                    EditorConfigurationCache.Instance.UseHttpTransport;

                bool changed =
                    !transportStateKnown ||
                    newUseHttpTransport != useHttpTransport;

                useHttpTransport = newUseHttpTransport;
                transportStateKnown = true;

                if (!useHttpTransport)
                {
                    serverReachable = false;
                    serverStateKnown = true;
                }

                if (changed)
                {
                    UpdateButtons();
                }

                return changed;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

                useHttpTransport = false;
                transportStateKnown = true;
                serverReachable = false;
                serverStateKnown = true;

                UpdateButtons();

                return false;
            }
        }

        // ------------------------------------------------------------
        // Status UI
        // ------------------------------------------------------------

        void UpdateStatusUI()
        {
            SetStatusColor(
                sessionRunning
                    ? ConnectedColor
                    : DisconnectedColor);

            try
            {
                TransportMode activeMode =
                    MCPServiceLocator.Bridge.ActiveMode
                    ?? TransportMode.Http;

                int currentPort =
                    MCPServiceLocator.Bridge.CurrentPort;

                string host =
                    activeMode == TransportMode.Http
                        ? HttpEndpointUtility.GetBaseUrl()
                        : $"stdio:{currentPort}";

                SetStatusText(
                    sessionRunning
                        ? $"MCP Session Active ({host})"
                        : $"MCP No Session ({host})");
            }
            catch (Exception ex)
            {
                SetStatusColor(DisconnectedColor);

                SetStatusText(
                    $"MCP status error: {ex.Message}");
            }
        }

        void UpdateButtons()
        {
            if (OperationInProgress())
                return;

            SetServerButtonText(
                serverReachable
                    ? "Stop Server"
                    : "Start Server");

            // This is the important part:
            // Start Server is enabled whenever HTTP transport is enabled.
            SetServerButtonEnabled(useHttpTransport);

            SetServerTooltip(
                useHttpTransport
                    ? string.Empty
                    : "Only applicable when transport is set to HTTP " +
                      "(see Window > MCP For Unity).");

            SetConnectButtonText(
                sessionRunning
                    ? "Disconnect"
                    : "Connect");

            SetConnectButtonEnabled(true);
        }

        // ------------------------------------------------------------
        // Session state
        // ------------------------------------------------------------

        void RefreshSessionState()
        {
            try
            {
                bool newSessionRunning =
                    MCPServiceLocator.Bridge.IsRunning;

                bool changed =
                    !sessionStateKnown ||
                    newSessionRunning != sessionRunning;

                sessionRunning = newSessionRunning;
                sessionStateKnown = true;

                if (changed)
                {
                    UpdateStatusUI();
                    UpdateButtons();
                }
            }
            catch (Exception ex)
            {
                sessionRunning = false;
                sessionStateKnown = true;

                SetStatusColor(DisconnectedColor);

                SetStatusText(
                    $"MCP status error: {ex.Message}");

                UpdateButtons();

                Debug.LogException(ex);
            }
        }

        // ------------------------------------------------------------
        // Endpoint
        // ------------------------------------------------------------

        bool TryGetServerEndpoint(
            out string host,
            out int port)
        {
            host = null;
            port = 0;

            try
            {
                // Use the same API as the original implementation.
                string url =
                    HttpEndpointUtility.GetBaseUrl();

                if (string.IsNullOrWhiteSpace(url))
                    return false;

                if (!Uri.TryCreate(
                        url,
                        UriKind.Absolute,
                        out Uri uri))
                {
                    return false;
                }

                host = uri.Host;
                port = uri.Port;

                return
                    !string.IsNullOrWhiteSpace(host) &&
                    port > 0 &&
                    port <= 65535;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------
        // Async TCP probe
        // ------------------------------------------------------------

        static async Task<bool> ProbeTcpAsync(
            string host,
            int port,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            foreach (string target in BuildProbeHosts(host))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using (TcpClient client = new TcpClient())
                    using (CancellationTokenSource timeoutCts =
                           CancellationTokenSource
                               .CreateLinkedTokenSource(
                                   cancellationToken))
                    {
                        Task connectTask =
                            client.ConnectAsync(
                                target,
                                port);

                        Task timeoutTask =
                            Task.Delay(
                                timeoutMs,
                                timeoutCts.Token);

                        Task completed =
                            await Task.WhenAny(
                                connectTask,
                                timeoutTask);

                        if (completed != connectTask)
                        {
                            continue;
                        }

                        timeoutCts.Cancel();

                        try
                        {
                            await connectTask;
                        }
                        catch
                        {
                            continue;
                        }

                        if (client.Connected)
                            return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Try next address.
                }
            }

            return false;
        }

        static IEnumerable<string> BuildProbeHosts(
            string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                yield return "127.0.0.1";
                yield break;
            }

            host = host.Trim();

            yield return host;

            if (string.Equals(
                    host,
                    "localhost",
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return "127.0.0.1";
                yield return "::1";
            }
            else if (string.Equals(
                         host,
                         "0.0.0.0",
                         StringComparison.OrdinalIgnoreCase))
            {
                yield return "127.0.0.1";
            }
            else if (string.Equals(
                         host,
                         "::",
                         StringComparison.OrdinalIgnoreCase))
            {
                yield return "::1";
            }
        }

        // ------------------------------------------------------------
        // Async server reachability
        // ------------------------------------------------------------

        async Task RefreshServerReachabilityAsync(
            bool force)
        {
            // Make absolutely sure transport state is current.
            RefreshTransportState();

            if (!useHttpTransport)
            {
                serverReachable = false;
                serverStateKnown = true;
                UpdateButtons();
                return;
            }

            if (serverProbeRunning)
                return;

            double now =
                EditorApplication.timeSinceStartup;

            if (!force &&
                now - lastServerPollTime <
                ServerPollIntervalSeconds)
            {
                return;
            }

            if (!TryGetServerEndpoint(
                    out string host,
                    out int port))
            {
                serverReachable = false;
                serverStateKnown = true;
                lastServerPollTime = now;

                UpdateButtons();
                return;
            }

            lastServerPollTime = now;
            serverProbeRunning = true;

            int generation =
                ++serverProbeGeneration;

            try
            {
                bool result =
                    await ProbeTcpAsync(
                        host,
                        port,
                        ServerProbeTimeoutMs,
                        lifetimeCts.Token);

                if (lifetimeCts.IsCancellationRequested)
                    return;

                if (generation != serverProbeGeneration)
                    return;

                bool changed =
                    !serverStateKnown ||
                    result != serverReachable;

                serverReachable = result;
                serverStateKnown = true;

                if (changed)
                {
                    UpdateButtons();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when the toolbar is detached.
            }
            catch (Exception ex)
            {
                if (generation != serverProbeGeneration)
                    return;

                serverReachable = false;
                serverStateKnown = true;

                Debug.LogException(ex);

                UpdateButtons();
            }
            finally
            {
                serverProbeRunning = false;
            }
        }

        // ------------------------------------------------------------
        // Wait for server startup
        // ------------------------------------------------------------

        async Task WaitForLocalServerThenConnectAsync()
        {
            double startTime =
                EditorApplication.timeSinceStartup;

            while (!lifetimeCts.IsCancellationRequested)
            {
                if (!TryGetServerEndpoint(
                        out string host,
                        out int port))
                {
                    return;
                }

                bool reachable =
                    await ProbeTcpAsync(
                        host,
                        port,
                        ServerProbeTimeoutMs,
                        lifetimeCts.Token);

                if (reachable)
                {
                    serverReachable = true;
                    serverStateKnown = true;

                    UpdateButtons();

                    bool started =
                        await MCPServiceLocator
                            .Bridge
                            .StartAsync();

                    if (started)
                    {
                        sessionRunning = true;
                        sessionStateKnown = true;

                        UpdateStatusUI();
                        UpdateButtons();
                    }

                    return;
                }

                // This API is only called while starting the server.
                bool processAlive =
                    MCPServiceLocator
                        .Server
                        .IsManagedServerLaunchProcessAlive();

                double elapsed =
                    EditorApplication.timeSinceStartup -
                    startTime;

                if ((!processAlive &&
                     elapsed > 1.0d)
                    ||
                    elapsed >
                    ServerLaunchHardCapSeconds)
                {
                    bool started =
                        await MCPServiceLocator
                            .Bridge
                            .StartAsync();

                    if (started)
                    {
                        sessionRunning = true;
                        sessionStateKnown = true;

                        UpdateStatusUI();
                        UpdateButtons();

                        return;
                    }

                    MCPServiceLocator
                        .Server
                        .LogLocalHttpServerLaunchFailure();

                    return;
                }

                await Task.Delay(
                    ServerLaunchPollIntervalMs,
                    lifetimeCts.Token);
            }
        }

        // ------------------------------------------------------------
        // Start / Stop Server
        // ------------------------------------------------------------

        async void OnServerButtonClicked()
        {
            if (OperationInProgress())
                return;

            // Refresh configuration immediately before acting.
            RefreshTransportState();

            if (!useHttpTransport)
            {
                EditorUtility.DisplayDialog(
                    "HTTP Transport Disabled",
                    "HTTP transport is currently disabled. " +
                    "Enable HTTP transport in Window > MCP For Unity.",
                    "OK");

                return;
            }

            serverOperationInProgress = true;

            SetServerButtonEnabled(false);
            SetConnectButtonEnabled(false);

            SetServerButtonText(
                serverReachable
                    ? "Stopping..."
                    : "Starting...");

            try
            {
                if (serverReachable)
                {
                    if (MCPServiceLocator
                            .Bridge
                            .IsRunning)
                    {
                        await MCPServiceLocator
                            .Bridge
                            .StopAsync();
                    }

                    MCPServiceLocator
                        .Server
                        .StopLocalHttpServer();

                    serverReachable = false;
                    serverStateKnown = true;

                    sessionRunning = false;
                    sessionStateKnown = true;

                    UpdateStatusUI();
                }
                else
                {
                    if (!MCPServiceLocator
                            .Server
                            .CanStartLocalServer())
                    {
                        EditorUtility.DisplayDialog(
                            "Cannot Start HTTP Server",
                            "HTTP transport is disabled or the " +
                            "configured URL is not allowed to launch " +
                            "a local server. Check Window > " +
                            "MCP For Unity.",
                            "OK");

                        return;
                    }

                    bool started =
                        MCPServiceLocator
                            .Server
                            .StartLocalHttpServer();

                    if (!started)
                    {
                        Debug.LogWarning(
                            "[MCP] StartLocalHttpServer() returned false.");

                        return;
                    }

                    // Wait asynchronously for the server.
                    await WaitForLocalServerThenConnectAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during domain reload / toolbar detach.
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                serverOperationInProgress = false;

                lastServerPollTime = -1d;
                lastSessionPollTime = -1d;

                RefreshTransportState();
                RefreshSessionState();

                // Force one authoritative async probe.
                _ = RefreshServerReachabilityAsync(true);
            }
        }

        // ------------------------------------------------------------
        // Connect / Disconnect
        // ------------------------------------------------------------

        async void OnConnectClicked()
        {
            if (OperationInProgress())
                return;

            sessionOperationInProgress = true;

            SetConnectButtonEnabled(false);

            SetConnectButtonText(
                sessionRunning
                    ? "Disconnecting..."
                    : "Connecting...");

            try
            {
                if (MCPServiceLocator
                        .Bridge
                        .IsRunning)
                {
                    await MCPServiceLocator
                        .Bridge
                        .StopAsync();

                    sessionRunning = false;
                }
                else
                {
                    await MCPServiceLocator
                        .Bridge
                        .StartAsync();

                    sessionRunning =
                        MCPServiceLocator
                            .Bridge
                            .IsRunning;
                }

                sessionStateKnown = true;

                UpdateStatusUI();
            }
            catch (OperationCanceledException)
            {
                // Expected during domain reload.
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                sessionOperationInProgress = false;

                lastSessionPollTime = -1d;

                RefreshSessionState();
                UpdateButtons();
            }
        }

        // ------------------------------------------------------------
        // Editor update
        // ------------------------------------------------------------

        void OnEditorUpdate()
        {
            if (lifetimeCts.IsCancellationRequested)
                return;

            if (EditorApplication.isCompiling ||
                EditorApplication.isUpdating)
            {
                return;
            }

            double now =
                EditorApplication.timeSinceStartup;

            // Transport configuration is cheap.
            // Keep it current so Start Server does not remain disabled.
            RefreshTransportState();

            // Cheap session state.
            if (now - lastSessionPollTime >=
                SessionPollIntervalSeconds)
            {
                lastSessionPollTime = now;
                RefreshSessionState();
            }

            // Async TCP probe.
            if (now - lastServerPollTime >=
                ServerPollIntervalSeconds)
            {
                _ = RefreshServerReachabilityAsync(false);
            }
        }

        // ------------------------------------------------------------
        // Attach
        // ------------------------------------------------------------

        void OnAttach(AttachToPanelEvent _)
        {
            // Prevent duplicate registrations.
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            if (lifetimeCts.IsCancellationRequested)
            {
                lifetimeCts.Dispose();

                lifetimeCts =
                    new CancellationTokenSource();
            }

            lastSessionPollTime = -1d;
            lastServerPollTime = -1d;

            // IMPORTANT:
            // Refresh transport BEFORE updating buttons.
            RefreshTransportState();

            RefreshSessionState();

            RefreshServerReachabilityAsync(true);
        }

        // ------------------------------------------------------------
        // Detach
        // ------------------------------------------------------------

        void OnDetach(DetachFromPanelEvent _)
        {
            EditorApplication.update -= OnEditorUpdate;

            // Invalidate all outstanding async probes.
            ++serverProbeGeneration;

            try
            {
                lifetimeCts.Cancel();
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------
        // Build UI
        // ------------------------------------------------------------

        serverButton = new Button(OnServerButtonClicked)
        {
            text = "...",
            style =
            {
                marginRight = 4
            }
        };

        connectButton = new Button(OnConnectClicked)
        {
            text = "..."
        };

        root.Add(statusDot);
        root.Add(statusLabel);
        root.Add(serverButton);
        root.Add(connectButton);

        root.RegisterCallback<AttachToPanelEvent>(
            OnAttach);

        root.RegisterCallback<DetachFromPanelEvent>(
            OnDetach);

        // Initial state.
        RefreshTransportState();
        RefreshSessionState();

        return root;
    }
}

#endif
