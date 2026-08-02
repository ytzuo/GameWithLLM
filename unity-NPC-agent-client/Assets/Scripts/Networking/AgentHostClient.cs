using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;

// 场景级门面：编排 A2A、Runtime Bridge、主线程工具执行和存档协调。
public class AgentHostClient : Singleton<AgentHostClient>
{
    public string a2aUrl = "http://127.0.0.1:8080/a2a";
    public string agentServiceBaseUrl = "http://127.0.0.1:8080";
    public string runtimeGatewayWsUrl = "ws://127.0.0.1:8080/runtime/ws";
    public string unityInstanceId = "local-game-1";
    public string playerId = "local-player-1";
    public string sceneId = "warehouse-demo";

    private readonly CancellationTokenSource _appCts = new CancellationTokenSource();
    private readonly ConcurrentDictionary<string, string> _contexts =
        new ConcurrentDictionary<string, string>();
    private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly object _manifestLock = new object();
    private A2AClientAdapter _a2a;
    private SaveCoordinationClient _saveCoordinator;
    private IRuntimeTransport _runtimeTransport;
    private CommandDispatcher _dispatcher;
    private ToolsRegistry _tools;
    private RuntimeManifest _manifest;
    private long _manifestRevision;
    private string _activeNpcId;
    private volatile bool _saveBusy;
    private volatile bool _restoreFailed;

    // Init 装配唯一的出站 Runtime 连接，并发布当前场景的实体与工具 Manifest。
    protected override void Init()
    {
        DotEnvConfig config = DotEnvConfig.Load();
        a2aUrl = config.Get("A2A_AGENT_URL", a2aUrl);
        agentServiceBaseUrl = config.Get("AGENT_SERVICE_BASE_URL", agentServiceBaseUrl);
        runtimeGatewayWsUrl = config.Get("RUNTIME_GATEWAY_WS_URL", runtimeGatewayWsUrl);
        unityInstanceId = config.Get("UNITY_INSTANCE_ID", unityInstanceId);
        playerId = config.Get("PLAYER_ID", playerId);
        sceneId = config.Get("UNITY_SCENE_ID", sceneId);
        unityInstanceId = $"{unityInstanceId}-{Guid.NewGuid():N}";

        foreach (PlayerMock player in FindObjectsByType<PlayerMock>(
                     FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None))
            player.ConfigureWorldTargetId(playerId);
        _dispatcher = CommandDispatcher.Instance;
        _tools = ToolsRegistry.Instance;
        _dispatcher.EntityChanged += OnRuntimeChanged;
        _dispatcher.EntityCapabilitiesChanged += OnCapabilitiesChanged;
        _tools.ToolsChanged += OnToolsChanged;
        RefreshManifest();
        _a2a = new A2AClientAdapter(
            a2aUrl,
            config.Get("A2A_BEARER_TOKEN", string.Empty),
            TimeSpan.FromSeconds(120));
        _saveCoordinator = new SaveCoordinationClient(
            agentServiceBaseUrl,
            config.Get("A2A_BEARER_TOKEN", string.Empty),
            TimeSpan.FromSeconds(30));

        var gateway = new RuntimeGatewayClient(
            runtimeGatewayWsUrl,
            config.Get("RUNTIME_GATEWAY_TOKEN", string.Empty));
        gateway.Info += message => Debug.Log($"[Agent Runtime] {message}");
        gateway.Warning += message => Debug.LogWarning($"[Agent Runtime] {message}");
        _runtimeTransport = gateway;
        _ = RunRuntimeAsync();
    }

    private void Update()
    {
        while (_mainThread.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception ex) { Debug.LogWarning($"[Agent Runtime] UI callback failed: {ex.Message}"); }
        }
    }

    public void OnPlayerInteractWithNpc(string npcId)
    {
        if (!_saveBusy && !_restoreFailed && !string.IsNullOrWhiteSpace(npcId))
            _activeNpcId = npcId;
    }

    public void SubmitPlayerInput(string text)
    {
        if (_saveBusy) { SystemMessage("存档操作进行中，请稍候。"); return; }
        if (_restoreFailed) { SystemMessage("对话历史尚未恢复，请重试加载。"); return; }
        if (string.IsNullOrWhiteSpace(_activeNpcId))
        {
            SystemMessage("当前没有正在交互的 NPC。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(text))
            _ = SubmitAsync(_activeNpcId, text);
    }

    // 对话与存档共用发送锁，避免快照期间新的 tool loop 改写世界状态。
    private async Task SubmitAsync(string npcId, string text)
    {
        await _sendLock.WaitAsync(_appCts.Token);
        try
        {
            _contexts.TryGetValue(npcId, out string contextId);
            ResponseCompleted completed = await _a2a.SendStreamingAsync(
                contextId,
                unityInstanceId,
                playerId,
                npcId,
                sceneId,
                text,
                responseEvent => HandleResponseEvent(npcId, responseEvent),
                _appCts.Token);
            if (!string.IsNullOrWhiteSpace(completed?.ContextId))
                _contexts[npcId] = completed.ContextId;
        }
        catch (OperationCanceledException) when (_appCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            CancelStream(npcId);
            SystemMessage(npcId, $"对话请求失败：{ex.Message}");
        }
        finally { _sendLock.Release(); }
    }

    public Task CancelActiveResponseAsync() =>
        _a2a.CancelActiveTaskAsync(_appCts.Token);

    // 网络线程只转换事件并投递 UI 回调，实际 Unity API 在 Update 中执行。
    private void HandleResponseEvent(string npcId, AgentResponseEvent responseEvent)
    {
        if (responseEvent is TextDelta delta)
        {
            if (delta.Reset) CancelStream(npcId);
            else if (!string.IsNullOrEmpty(delta.Text))
                _mainThread.Enqueue(
                    () => ChatViewModel.Instance.AppendOpponentMessageDelta(npcId, delta.Text));
        }
        else if (responseEvent is ResponseCompleted completed)
        {
            if (!string.IsNullOrWhiteSpace(completed.ContextId))
                _contexts[npcId] = completed.ContextId;
            _mainThread.Enqueue(
                () => ChatViewModel.Instance.CompleteOpponentMessageStream(
                    npcId,
                    completed.FinalText));
        }
        else if (responseEvent is ResponseFailed failed)
        {
            CancelStream(npcId);
            SystemMessage(npcId, $"Agent 请求失败 ({failed.Code})：{failed.Message}");
        }
    }

    public bool IsSaveGameOperationInProgress => _saveBusy;

    // 先冻结新对话并保存 Unity 世界，再 prepare/commit 对应的 Agent 快照。
    public async Task<AgentSnapshotSaveResult> SaveWorldAndConversationsForSaveGameAsync(
        Func<SaveGameFile> saveWorld)
    {
        if (saveWorld == null)
            throw new ArgumentNullException(nameof(saveWorld));
        _saveBusy = true;
        bool lockHeld = false;
        try
        {
            await _sendLock.WaitAsync(_appCts.Token);
            lockHeld = true;
            // The lock waits for the active A2A/tool loop to finish and prevents a
            // new one from changing the world between world capture and snapshot.
            SaveGameFile file = saveWorld();
            try
            {
                return await _saveCoordinator.PrepareAndCommitAsync(
                    file.SaveId,
                    file.OperationId,
                    unityInstanceId,
                    playerId,
                    file.PendingConversationMode,
                    _appCts.Token);
            }
            catch (Exception firstError)
            {
                Debug.LogWarning(
                    $"首次对话快照请求失败，将复用 operationId 重试一次: {firstError.Message}");
                return await _saveCoordinator.PrepareAndCommitAsync(
                    file.SaveId,
                    file.OperationId,
                    unityInstanceId,
                    playerId,
                    file.PendingConversationMode,
                    _appCts.Token);
            }
        }
        finally
        {
            if (lockHeld)
                _sendLock.Release();
            _saveBusy = false;
        }
    }

    public async Task<AgentSnapshotSaveResult> SaveConversationsForSaveGameAsync(
        string saveId,
        string operationId,
        string mode)
    {
        _saveBusy = true;
        bool lockHeld = false;
        try
        {
            await _sendLock.WaitAsync(_appCts.Token);
            lockHeld = true;
            return await _saveCoordinator.PrepareAndCommitAsync(
                saveId, operationId, unityInstanceId, playerId, mode, _appCts.Token);
        }
        finally
        {
            if (lockHeld)
                _sendLock.Release();
            _saveBusy = false;
        }
    }

    // 恢复顺序固定为世界与实体优先，随后恢复对话并替换 Context ID。
    public async Task<AgentSnapshotLoadResult> LoadConversationsForSaveGameAsync(
        string saveId,
        IReadOnlyList<string> npcIds,
        Action applyWorldState)
    {
        await _sendLock.WaitAsync(_appCts.Token);
        _saveBusy = true;
        _restoreFailed = true;
        try
        {
            _contexts.Clear();
            ChatViewModel.Instance.ClearAllHistory();
            applyWorldState?.Invoke();
            AgentSnapshotLoadResult result = await _saveCoordinator.RestoreAsync(
                saveId,
                Guid.NewGuid().ToString(),
                unityInstanceId,
                playerId,
                npcIds ?? Array.Empty<string>(),
                _appCts.Token);
            if (!result.Ok) return result;
            if (result.Contexts != null)
            {
                foreach (AgentLoadedConversationContext context in result.Contexts)
                    if (!string.IsNullOrWhiteSpace(context?.NpcId) &&
                        !string.IsNullOrWhiteSpace(context.ContextId))
                        _contexts[context.NpcId] = context.ContextId;
            }
            ChatViewModel.Instance.ReplaceHistories(result.Contexts);
            _restoreFailed = false;
            return result;
        }
        finally { _saveBusy = false; _sendLock.Release(); }
    }

    private void OnRuntimeChanged(string _, bool __) => PublishManifest();
    private void OnCapabilitiesChanged(string _) => PublishManifest();
    private void OnToolsChanged() => PublishManifest();

    // 持续消费 Transport 命令；连接与重连由 RuntimeGatewayClient 内部维护。
    private async Task RunRuntimeAsync()
    {
        try
        {
            await _runtimeTransport.StartAsync(GetManifest(), _appCts.Token);
            await foreach (RuntimeCommand command in
                           _runtimeTransport.ReadCommandsAsync(_appCts.Token))
            {
                _ = ExecuteRuntimeCommandAsync(command);
            }
        }
        catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Agent Runtime] Transport stopped: {ex}");
        }
    }

    // 将网络命令交给主线程 Dispatcher，并把最终业务结果返回 Gateway。
    private async Task ExecuteRuntimeCommandAsync(RuntimeCommand command)
    {
        try
        {
            AgentToolResult result = await _dispatcher.ExecuteAsync(
                command,
                (progress, message) =>
                    _ = SendRuntimeProgressAsync(
                        command.InvocationId,
                        progress,
                        message),
                _appCts.Token);
            await _runtimeTransport.SendResultAsync(
                command.InvocationId,
                result,
                _appCts.Token);
        }
        catch (OperationCanceledException) when (
            command.CancellationToken.IsCancellationRequested ||
            _appCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[Agent Runtime] Invocation '{command.InvocationId}' failed: {ex.Message}");
            try
            {
                await _runtimeTransport.SendResultAsync(
                    command.InvocationId,
                    AgentToolResult.Failure(
                        "RUNTIME_EXECUTION_FAILED",
                        "Unity Runtime failed to execute the tool."),
                    _appCts.Token);
            }
            catch (Exception sendError)
            {
                Debug.LogWarning(
                    $"[Agent Runtime] Invocation error response " +
                    $"'{command.InvocationId}' failed: {sendError.Message}");
            }
        }
    }

    private async Task SendRuntimeProgressAsync(
        string invocationId,
        double progress,
        string message)
    {
        try
        {
            await _runtimeTransport.SendProgressAsync(
                invocationId,
                progress,
                message,
                _appCts.Token);
        }
        catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                $"[Agent Runtime] Progress '{invocationId}' failed: {ex.Message}");
        }
    }

    // 每次实体或工具能力变化都生成并发布完整 Manifest，而不是增量补丁。
    private void PublishManifest()
    {
        RefreshManifest();
        if (_runtimeTransport != null)
            _ = _runtimeTransport.UpdateManifestAsync(
                GetManifest(),
                _appCts.Token);
    }
    private void RefreshManifest()
    {
        lock (_manifestLock)
        {
            _manifest = new RuntimeManifest(
                unityInstanceId,
                _dispatcher.GetRegisteredEntityIds(),
                _tools.GetRuntimeTools(),
                Interlocked.Increment(ref _manifestRevision));
        }
    }
    private RuntimeManifest GetManifest()
    {
        lock (_manifestLock)
            return new RuntimeManifest(
                _manifest.InstanceId,
                new List<string>(_manifest.EntityIds),
                new List<AgentToolDescriptor>(_manifest.Tools),
                _manifest.Revision);
    }
    private void CancelStream(string npcId) =>
        _mainThread.Enqueue(() => ChatViewModel.Instance.CancelOpponentMessageStream(npcId));
    private void SystemMessage(string text) => SystemMessage(_activeNpcId, text);
    private void SystemMessage(string npcId, string text) =>
        _mainThread.Enqueue(() => ChatViewModel.Instance.AddSystemMessage(npcId, text));

    private void OnDestroy()
    {
        if (_dispatcher != null)
        {
            _dispatcher.EntityChanged -= OnRuntimeChanged;
            _dispatcher.EntityCapabilitiesChanged -= OnCapabilitiesChanged;
        }
        if (_tools != null) _tools.ToolsChanged -= OnToolsChanged;
        _appCts.Cancel();
        (_runtimeTransport as IDisposable)?.Dispose();
        _a2a?.Dispose();
        _saveCoordinator?.Dispose();
        _sendLock.Dispose();
        _appCts.Dispose();
    }
}
