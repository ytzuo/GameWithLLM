using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class AgentHostClient : Singleton<AgentHostClient>
{
    [Header("Gateway 配置")]
    public string gatewayWsUrl = "ws://127.0.0.1:8080/unity/ws";
    public string unityInstanceId = "local-game-1";
    public string playerId = "local-player-1";

    private readonly CancellationTokenSource _appCts = new CancellationTokenSource();
    private readonly ConcurrentDictionary<string, string> _sessionsByNpc = new ConcurrentDictionary<string, string>();
    private readonly ConcurrentDictionary<string, Task<string>> _sessionStarts = new ConcurrentDictionary<string, Task<string>>();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
    private readonly SemaphoreSlim _conversationSendLock = new SemaphoreSlim(1, 1);
    private readonly object _capabilitySnapshotLock = new object();

    private UnityGatewayClient _gatewayClient;
    private CommandDispatcher _dispatcher;
    private ToolsRegistry _toolsRegistry;
    private UnityGatewayCapabilitySnapshot _capabilitySnapshot =
        new UnityGatewayCapabilitySnapshot
        {
            Tools = new List<UnityGatewayToolDefinition>(),
            Npcs = new List<string>(),
            NpcTools = new Dictionary<string, List<string>>()
        };
    private string _activeNpcId;

    protected override void Init()
    {
        DotEnvConfig config = DotEnvConfig.Load();
        gatewayWsUrl = config.Get("UNITY_JSONRPC_WS_URL", gatewayWsUrl);
        unityInstanceId = config.Get("UNITY_INSTANCE_ID", unityInstanceId);
        playerId = config.Get("PLAYER_ID", playerId);
        PlayerMock[] players = FindObjectsByType<PlayerMock>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
            players[i].ConfigureWorldTargetId(playerId);
        unityInstanceId = $"{unityInstanceId}-{Guid.NewGuid():N}";

        _dispatcher = CommandDispatcher.Instance;
        _toolsRegistry = ToolsRegistry.Instance;
        _dispatcher.NpcChanged += OnNpcChanged;
        _dispatcher.NpcCapabilitiesChanged += OnNpcCapabilitiesChanged;
        _toolsRegistry.ToolsChanged += OnToolsChanged;
        RefreshCapabilitySnapshot();

        _gatewayClient = new UnityGatewayClient(
            gatewayWsUrl,
            unityInstanceId,
            GetCapabilitySnapshot);
        _gatewayClient.ToolCallReceived += OnGatewayToolCallReceived;
        _gatewayClient.ToolCancellationReceived += OnGatewayToolCancellationReceived;
        _gatewayClient.AssistantStatusReceived += OnAssistantStatusReceived;
        _gatewayClient.AssistantDeltaReceived += OnAssistantDeltaReceived;
        _gatewayClient.Registered += OnGatewayRegistered;
        _gatewayClient.Info += OnGatewayInfo;
        _gatewayClient.Warning += OnGatewayWarning;
        _gatewayClient.Start(_appCts.Token);
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out Action action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Agent Host] 主线程 UI 回调失败: {ex.Message}");
            }
        }
    }

    public void OnPlayerInteractWithNpc(string npcId)
    {
        if (string.IsNullOrWhiteSpace(npcId))
            return;
        _activeNpcId = npcId;
        _ = PrepareConversationAsync(npcId);
    }

    public void SubmitPlayerInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        string npcId = _activeNpcId;
        if (string.IsNullOrWhiteSpace(npcId))
        {
            EnqueueSystemMessage("当前没有正在交互的 NPC。");
            return;
        }
        _ = SubmitPlayerInputAsync(npcId, text);
    }

    private async Task PrepareConversationAsync(string npcId)
    {
        try
        {
            await EnsureSessionAsync(npcId);
        }
        catch (Exception ex)
        {
            EnqueueSystemMessage($"无法开始对话：{ex.Message}");
        }
    }

    private async Task SubmitPlayerInputAsync(string npcId, string text)
    {
        await _conversationSendLock.WaitAsync(_appCts.Token);
        string sessionId = null;
        try
        {
            sessionId = await EnsureSessionAsync(npcId);
            UnityGatewayAssistantReply reply = await _gatewayClient.SendPlayerMessageAsync(
                sessionId,
                text,
                _appCts.Token);
            EnqueueOpponentStreamCompleted(npcId, reply?.Text);
        }
        catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
        {
        }
        catch (UnityGatewayRequestException ex) when (IsConversationInvalid(ex.Code))
        {
            if (_sessionsByNpc.TryGetValue(npcId, out string currentSession) &&
                string.Equals(currentSession, sessionId, StringComparison.Ordinal))
                _sessionsByNpc.TryRemove(npcId, out _);
            await EndConversationSafelyAsync(sessionId);
            EnqueueOpponentStreamCancelled(npcId);
            EnqueueSystemMessage(npcId, "对话会话已失效，请重新发送。下一条消息会自动创建新会话。");
        }
        catch (UnityGatewayRequestException ex) when (ex.Code == -32022)
        {
            EnqueueOpponentStreamCancelled(npcId);
            EnqueueSystemMessage(npcId, "模型服务暂时不可用，请稍后重试。当前对话上下文已保留。");
        }
        catch (Exception ex)
        {
            EnqueueOpponentStreamCancelled(npcId);
            EnqueueSystemMessage(npcId, $"对话请求失败：{ex.Message}。当前对话上下文已保留。");
        }
        finally
        {
            _conversationSendLock.Release();
        }
    }

    private static bool IsConversationInvalid(int errorCode)
    {
        return errorCode == -32011 || errorCode == -32012;
    }

    private async Task EndConversationSafelyAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || _gatewayClient == null)
            return;
        try
        {
            await _gatewayClient.EndConversationAsync(sessionId, _appCts.Token);
        }
        catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Agent Host] 清理失效会话失败: sessionId={sessionId}, error={ex.Message}");
        }
    }

    private async Task<string> EnsureSessionAsync(string npcId)
    {
        if (_sessionsByNpc.TryGetValue(npcId, out string existingSession))
            return existingSession;

        Task<string> startTask = _sessionStarts.GetOrAdd(npcId, StartConversationCoreAsync);
        try
        {
            string sessionId = await startTask;
            _sessionsByNpc[npcId] = sessionId;
            return sessionId;
        }
        finally
        {
            _sessionStarts.TryRemove(npcId, out _);
        }
    }

    private async Task<string> StartConversationCoreAsync(string npcId)
    {
        UnityGatewayConversationStartResult result = await _gatewayClient.StartConversationAsync(
            playerId,
            npcId,
            _appCts.Token);
        if (string.IsNullOrWhiteSpace(result?.SessionId))
            throw new InvalidOperationException("Go Agent Host 未返回 sessionId。");
        Debug.Log($"[Agent Host] 会话已开始: sessionId={result.SessionId}, npcId={npcId}");
        return result.SessionId;
    }

    public async Task SendToolResponseAsync(
        string requestId,
        string text,
        bool isError = false,
        string errorCode = null,
        JToken data = null)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        EnqueueToolResultTrace(
            "unity_tool_result_sending",
            requestId,
            text,
            isError,
            errorCode,
            data);
        try
        {
            await _gatewayClient.SendToolResultAsync(
                requestId,
                text,
                isError,
                errorCode,
                data,
                _appCts.Token);
            EnqueueToolResultTrace(
                "unity_tool_result_sent",
                requestId,
                text,
                isError,
                errorCode,
                data);
        }
        catch (Exception ex)
        {
            EnqueueToolResultTrace(
                "unity_tool_result_send_failed",
                requestId,
                text,
                isError,
                errorCode,
                data,
                ex.Message);
            string errorMessage = ex.Message;
            _mainThreadActions.Enqueue(
                () => Debug.LogError($"[Unity Gateway] 发送工具响应失败: requestId={requestId}, error={errorMessage}"));
        }
    }

    private void OnGatewayRegistered()
    {
        _sessionsByNpc.Clear();
        _sessionStarts.Clear();
    }

    private void OnAssistantStatusReceived(UnityGatewayAssistantStatus status)
    {
        if (status?.Status == "thinking" &&
            TryGetNpcIdForSession(status.SessionId, out string npcId))
            EnqueueSystemMessage(npcId, "NPC 正在思考……");
    }

    private void OnAssistantDeltaReceived(UnityGatewayAssistantDelta delta)
    {
        if (delta == null)
            return;
        if (!TryGetNpcIdForSession(delta.SessionId, out string npcId))
            return;

        if (delta.Reset)
        {
            _mainThreadActions.Enqueue(
                () => ChatViewModel.Instance.CancelOpponentMessageStream(npcId));
            return;
        }
        if (!string.IsNullOrEmpty(delta.Text))
        {
            _mainThreadActions.Enqueue(
                () => ChatViewModel.Instance.AppendOpponentMessageDelta(npcId, delta.Text));
        }
    }

    private void OnGatewayToolCallReceived(UnityToolCommand request)
    {
        string argumentsJson = request?.Function?.ArgumentsJson;
        var trace = new JObject
        {
            ["event"] = "unity_tool_received",
            ["requestId"] = request?.RequestId,
            ["npcId"] = request?.NpcId,
            ["tool"] = request?.Function?.Name,
            ["argumentsLength"] = argumentsJson?.Length ?? 0
        };
        EnqueueToolTrace(trace);
        _dispatcher.OnReceiveNetMessage(request);
    }

    private void EnqueueToolResultTrace(
        string eventName,
        string requestId,
        string text,
        bool isError,
        string errorCode,
        JToken data,
        string transportError = null)
    {
        var trace = new JObject
        {
            ["event"] = eventName,
            ["requestId"] = requestId,
            ["ok"] = !isError
        };
        if (!string.IsNullOrEmpty(errorCode))
            trace["errorCode"] = errorCode;
        trace["messageLength"] = text?.Length ?? 0;
        trace["dataLength"] = data?.ToString(Formatting.None).Length ?? 0;
        if (!string.IsNullOrEmpty(transportError))
            trace["transportError"] = transportError;
        EnqueueToolTrace(trace);
    }

    private void EnqueueToolTrace(JObject trace)
    {
        string compactJson = trace.ToString(Formatting.None);
        _mainThreadActions.Enqueue(
            () => Debug.Log($"[Unity Tool Trace] {compactJson}"));
    }

    private void OnGatewayToolCancellationReceived(string requestId)
    {
        _dispatcher.CancelRequest(requestId);
    }

    private void OnNpcChanged(string npcId, bool online)
    {
        RefreshCapabilitySnapshot();
        if (!online && _sessionsByNpc.TryRemove(npcId, out string sessionId) && _gatewayClient != null)
            _ = _gatewayClient.EndConversationAsync(sessionId, _appCts.Token);
        if (_gatewayClient != null)
            _ = _gatewayClient.NotifyNpcChangedAsync(npcId, online, _appCts.Token);
    }

    private void OnNpcCapabilitiesChanged(string npcId)
    {
        RefreshCapabilitySnapshot();
        if (_gatewayClient != null)
            _ = _gatewayClient.NotifyToolsChangedAsync(_appCts.Token);
    }

    private void OnToolsChanged()
    {
        RefreshCapabilitySnapshot();
        if (_gatewayClient != null)
            _ = _gatewayClient.NotifyToolsChangedAsync(_appCts.Token);
    }

    private void RefreshCapabilitySnapshot()
    {
        List<UnityGatewayToolDefinition> tools = _toolsRegistry.GetToolsForGateway();
        Dictionary<string, NpcEntity> registered = _dispatcher.GetRegisteredNpcsSnapshot();
        var npcIds = new List<string>(registered.Keys);
        npcIds.Sort(StringComparer.Ordinal);
        var npcTools = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 0; i < npcIds.Count; i++)
        {
            string npcId = npcIds[i];
            npcTools[npcId] = _toolsRegistry.GetToolNamesForNpc(registered[npcId]);
        }

        var snapshot = new UnityGatewayCapabilitySnapshot
        {
            Tools = tools,
            Npcs = npcIds,
            NpcTools = npcTools
        };
        lock (_capabilitySnapshotLock)
            _capabilitySnapshot = snapshot;
    }

    private UnityGatewayCapabilitySnapshot GetCapabilitySnapshot()
    {
        lock (_capabilitySnapshotLock)
            return _capabilitySnapshot;
    }

    private bool TryGetNpcIdForSession(string sessionId, out string npcId)
    {
        foreach (KeyValuePair<string, string> pair in _sessionsByNpc)
        {
            if (string.Equals(pair.Value, sessionId, StringComparison.Ordinal))
            {
                npcId = pair.Key;
                return true;
            }
        }
        npcId = null;
        return false;
    }

    private void EnqueueOpponentStreamCompleted(string npcId, string finalText)
    {
        _mainThreadActions.Enqueue(
            () => ChatViewModel.Instance.CompleteOpponentMessageStream(npcId, finalText));
    }

    private void EnqueueOpponentStreamCancelled(string npcId)
    {
        _mainThreadActions.Enqueue(
            () => ChatViewModel.Instance.CancelOpponentMessageStream(npcId));
    }

    private void EnqueueSystemMessage(string text)
    {
        string npcId = _activeNpcId;
        EnqueueSystemMessage(npcId, text);
    }

    private void EnqueueSystemMessage(string npcId, string text)
    {
        _mainThreadActions.Enqueue(() => ChatViewModel.Instance.AddSystemMessage(npcId, text));
    }

    private static void OnGatewayInfo(string message) => Debug.Log($"[Unity Gateway] {message}");
    private static void OnGatewayWarning(string message) => Debug.LogWarning($"[Unity Gateway] {message}");

    void OnDestroy()
    {
        if (_dispatcher != null)
        {
            _dispatcher.NpcChanged -= OnNpcChanged;
            _dispatcher.NpcCapabilitiesChanged -= OnNpcCapabilitiesChanged;
        }
        if (_toolsRegistry != null)
            _toolsRegistry.ToolsChanged -= OnToolsChanged;

        if (_gatewayClient != null)
        {
            _gatewayClient.ToolCallReceived -= OnGatewayToolCallReceived;
            _gatewayClient.ToolCancellationReceived -= OnGatewayToolCancellationReceived;
            _gatewayClient.AssistantStatusReceived -= OnAssistantStatusReceived;
            _gatewayClient.AssistantDeltaReceived -= OnAssistantDeltaReceived;
            _gatewayClient.Registered -= OnGatewayRegistered;
            _gatewayClient.Info -= OnGatewayInfo;
            _gatewayClient.Warning -= OnGatewayWarning;
        }

        _appCts.Cancel();
        _gatewayClient?.Dispose();
    }
}

internal sealed class DotEnvConfig
{
    private readonly Dictionary<string, string> _values;

    private DotEnvConfig(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static DotEnvConfig Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = FindRepoRoot();
        if (!string.IsNullOrEmpty(root))
        {
            LoadFile(Path.Combine(root, ".env"), values);
            LoadFile(Path.Combine(root, ".env.local"), values);
        }
        return new DotEnvConfig(values);
    }

    public string Get(string key, string fallback)
    {
        string envValue = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue.Trim();
        if (_values.TryGetValue(key, out string fileValue) && !string.IsNullOrWhiteSpace(fileValue)) return fileValue.Trim();
        return fallback;
    }

    private static string FindRepoRoot()
    {
        var candidates = new List<string>
        {
            Directory.GetCurrentDirectory(),
            Application.dataPath
        };

        foreach (string candidate in candidates)
        {
            string root = WalkUp(candidate);
            if (!string.IsNullOrEmpty(root)) return root;
        }
        return null;
    }

    private static string WalkUp(string startPath)
    {
        if (string.IsNullOrEmpty(startPath)) return null;
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            string serverDir = Path.Combine(dir.FullName, "GameMCPServer");
            string unityDir = Path.Combine(dir.FullName, "unity-NPC-agent-client");
            if (Directory.Exists(serverDir) && Directory.Exists(unityDir))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static void LoadFile(string path, Dictionary<string, string> values)
    {
        if (!File.Exists(path)) return;

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int separator = line.IndexOf('=');
            if (separator <= 0) continue;

            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim().Trim('"', '\'');
            if (key.Length > 0) values[key] = value;
        }
    }
}
