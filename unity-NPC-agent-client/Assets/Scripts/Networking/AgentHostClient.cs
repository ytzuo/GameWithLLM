using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;

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
    private RuntimeGatewayClient _gateway;
    private CommandDispatcher _dispatcher;
    private ToolsRegistry _tools;
    private RuntimeManifestSnapshot _manifest = new RuntimeManifestSnapshot();
    private long _manifestRevision;
    private string _activeNpcId;
    private volatile bool _saveBusy;
    private volatile bool _restoreFailed;

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
        _dispatcher.NpcChanged += OnRuntimeChanged;
        _dispatcher.NpcCapabilitiesChanged += OnCapabilitiesChanged;
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

        _gateway = new RuntimeGatewayClient(
            runtimeGatewayWsUrl,
            config.Get("RUNTIME_GATEWAY_TOKEN", string.Empty),
            GetManifest,
            _dispatcher);
        _gateway.Info += message => Debug.Log($"[Agent Runtime] {message}");
        _gateway.Warning += message => Debug.LogWarning($"[Agent Runtime] {message}");
        _gateway.Start(_appCts.Token);
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
    private void PublishManifest()
    {
        RefreshManifest();
        if (_gateway != null)
            _ = _gateway.NotifyManifestChangedAsync(_appCts.Token);
    }
    private void RefreshManifest()
    {
        lock (_manifestLock)
        {
            _manifest = new RuntimeManifestSnapshot
            {
                InstanceId = unityInstanceId,
                Revision = Interlocked.Increment(ref _manifestRevision),
                Entities = _dispatcher.GetRegisteredNpcIds(),
                Tools = _tools.GetRuntimeTools()
            };
        }
    }
    private RuntimeManifestSnapshot GetManifest()
    {
        lock (_manifestLock)
            return new RuntimeManifestSnapshot
            {
                InstanceId = _manifest.InstanceId,
                Revision = _manifest.Revision,
                Entities = new List<string>(_manifest.Entities),
                Tools = new List<RuntimeToolDefinition>(_manifest.Tools)
            };
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
            _dispatcher.NpcChanged -= OnRuntimeChanged;
            _dispatcher.NpcCapabilitiesChanged -= OnCapabilitiesChanged;
        }
        if (_tools != null) _tools.ToolsChanged -= OnToolsChanged;
        _appCts.Cancel();
        _gateway?.Dispose();
        _a2a?.Dispose();
        _saveCoordinator?.Dispose();
        _sendLock.Dispose();
        _appCts.Dispose();
    }
}
