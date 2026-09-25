using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;

// 场景级门面：编排 A2A、Runtime Bridge、主线程工具执行和存档协调。
public class AgentHostClient : Singleton<AgentHostClient>
{
    [Header("Hot update")]
    [SerializeField] private bool enableHybridClrBootstrap = true;
    [SerializeField] private bool enableContentBootstrap = true;

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
    private readonly Dictionary<NpcEntity, List<string>> _npcCapabilities =
        new Dictionary<NpcEntity, List<string>>();
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
    private volatile bool _contentReady;
    private bool _initializationStarted;
    private bool _runtimeInitialized;
    private ContentAssetProvider _contentProvider;
    private ClientContentBootstrap _contentBootstrap;
    private ContentBootstrapOverlay _contentOverlay;
    private PlayerMock[] _gatedPlayers = Array.Empty<PlayerMock>();
    private UIManager _gatedUi;
    private float _nextCapabilityCheckAt;
    private LoadedToolSetRelease _contentRelease;
    private UiContentCatalog _uiContentCatalog;

    public bool IsContentReady => _contentReady;
    public ClientContentBootstrapState ContentBootstrapState =>
        _contentBootstrap?.State ?? ClientContentBootstrapState.NotStarted;

    // 内容激活前不创建 A2A/Runtime 客户端，也不开放游戏输入和业务 UI。
    protected override void Init()
    {
        if (_initializationStarted)
            return;
        _initializationStarted = true;
        GateGameplay(false);
        _ = InitializeAfterContentAsync();
    }

    private async Task InitializeAfterContentAsync()
    {
        try
        {
            if (enableContentBootstrap)
            {
                _contentProvider = new ContentAssetProvider();
                _contentBootstrap = new ClientContentBootstrap(
                    _contentProvider,
                    new PlayerPrefsContentBootstrapStore(),
                    new ClientContentBootstrapOptions
                    {
                        RequiredDownloadLabels = new[]
                        {
                            "content.a1-required",
                            "content.ui-required"
                        },
                        ReleaseLoader = new AddressableHotUpdateReleaseLoader(_contentProvider)
                    });
                _contentOverlay = GetComponent<ContentBootstrapOverlay>() ??
                                  gameObject.AddComponent<ContentBootstrapOverlay>();
                _contentOverlay.Bind(_contentBootstrap);

                while (!_appCts.IsCancellationRequested)
                {
                    ClientContentBootstrapResult result =
                        await _contentBootstrap.RunAttemptAsync(_appCts.Token);
                    if (result.Succeeded)
                    {
                        if (result.UsedCachedCatalog)
                            Debug.LogWarning("[Content] Started with the last successful cached catalog.");
                        _contentRelease = result.LoadedRelease;
                        if (result.CandidateError != null)
                            Debug.LogWarning(
                                "[Content] Remote hot-update candidate was rejected; " +
                                $"runtime will use builtin tools and stable text keys " +
                                $"(code={result.CandidateErrorCode}).");
                        break;
                    }

                    Debug.LogError(
                        $"[Content] Bootstrap failed; runtime remains disabled: " +
                        result.Error?.GetBaseException().Message);
                    _contentOverlay.ShowFailure(result.Error);
                    await _contentOverlay.WaitForRetryAsync(_appCts.Token);
                }
            }

            _appCts.Token.ThrowIfCancellationRequested();
            if (_contentProvider == null)
            {
                _contentProvider = new ContentAssetProvider();
                await _contentProvider.InitializeAsync(_appCts.Token);
            }
            _uiContentCatalog = new UiContentCatalog(_contentProvider);
            await _uiContentCatalog.PreloadAsync(_appCts.Token);
            _gatedUi ??= FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
            if (_gatedUi == null)
                throw new InvalidOperationException("UIManager is missing from the active scene.");
            _gatedUi.InitializeContent(_uiContentCatalog);
            Debug.Log("[Content] UI catalog preloaded and contracts validated.");

            InitializeRuntimeServices();
            _contentReady = true;
            GateGameplay(true);
            _contentOverlay?.Hide();
        }
        catch (OperationCanceledException) when (_appCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Content] Fatal bootstrap error; runtime remains disabled: {ex}");
            _contentOverlay?.ShowFailure(ex);
        }
    }

    // 装配唯一的出站 Runtime 连接，并发布当前场景的实体与工具 Manifest。
    private void InitializeRuntimeServices()
    {
        if (_runtimeInitialized)
            return;

        LoadedToolSetRelease loadedRelease = _contentRelease;
        if (!enableContentBootstrap)
        {
            try
            {
                loadedRelease = HybridClrBootstrap.LoadLocalRelease(enableHybridClrBootstrap);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Hot Update] HybridCLR bootstrap failed; continuing with AOT tools only: {ex}");
            }
        }

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
        if (loadedRelease != null)
        {
            try
            {
                ToolSetCandidate candidate = loadedRelease.BuildCandidate(
                    AgentToolDiscovery.DiscoverBuiltinTools());
                PreparedToolSet prepared = _tools.PrepareToolSet(
                    candidate,
                    loadedRelease.ToolMetadataJson);
                ClientTextCatalog agentMessages = ClientTextCatalog.Parse(
                    loadedRelease.AgentMessagesJson,
                    loadedRelease.CatalogVersion);
                ClientTextCatalog ui = ClientTextCatalog.Parse(
                    loadedRelease.UiJson,
                    loadedRelease.CatalogVersion);
                ToolSetActivationResult result = _tools.ActivateToolSet(prepared);
                ClientTextCatalogs.Activate(agentMessages, ui);
                ToolSetActivationStore.Save(_tools.ActiveSnapshot, _tools.ActiveCatalog);
                _contentBootstrap?.ConfirmActivation();
                Debug.Log(
                    $"[Hot Update] Release '{result.ReleaseId}' ToolSet '{result.ToolSetVersion}' " +
                    $"{(result.Idempotent ? "was already active" : "activated atomically")} " +
                    $"with {result.ToolCount} tools.");
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[Hot Update] Release '{loadedRelease.ReleaseId}' was rejected; " +
                    $"the last complete Registry snapshot remains active " +
                    $"(code=HOT_UPDATE_TOOLSET_REJECTED): {ex}");
            }
        }
        if (_tools.ActiveCatalog.IsIdentifierFallback)
        {
            Debug.LogWarning(
                "[Hot Update] tool_metadata JSON was not loaded; tool identifiers are being used " +
                "as temporary descriptions and parameter descriptions are omitted.");
        }
        _dispatcher.EntityChanged += OnRuntimeChanged;
        _dispatcher.EntityCapabilitiesChanged += OnCapabilitiesChanged;
        _tools.ToolsChanged += OnToolsChanged;
        NpcEntity.RuntimeAvailabilityChanged += OnNpcRuntimeAvailabilityChanged;
        foreach (NpcEntity npc in FindObjectsByType<NpcEntity>(
                     FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None))
            RegisterNpc(npc);
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
        _runtimeInitialized = true;
        _ = RunRuntimeAsync();
    }

    private void GateGameplay(bool ready)
    {
        if (!ready)
        {
            _gatedPlayers = FindObjectsByType<PlayerMock>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None)
                .Where(player => player != null && player.enabled)
                .ToArray();
            foreach (PlayerMock player in _gatedPlayers)
                player.enabled = false;
            _gatedUi = FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
            _gatedUi?.SetContentReady(false);
            return;
        }

        _gatedUi ??= FindFirstObjectByType<UIManager>(FindObjectsInactive.Include);
        _gatedUi?.SetContentReady(true);
        foreach (PlayerMock player in _gatedPlayers)
        {
            if (player != null)
                player.enabled = true;
        }
        _gatedPlayers = Array.Empty<PlayerMock>();
    }

    private void Update()
    {
        while (_mainThread.TryDequeue(out Action action))
        {
            try { action(); }
            catch (Exception ex) { Debug.LogWarning($"[Agent Runtime] UI callback failed: {ex.Message}"); }
        }
        if (_runtimeInitialized)
            RefreshNpcCapabilitiesIfNeeded();
    }

    private void OnNpcRuntimeAvailabilityChanged(NpcEntity npc, bool available)
    {
        if (available)
        {
            RegisterNpc(npc);
            return;
        }

        _npcCapabilities.Remove(npc);
        if (npc != null)
            _dispatcher.UnregisterEntity(npc.npcId, npc);
    }

    private void RegisterNpc(NpcEntity npc)
    {
        if (npc == null || !npc.isActiveAndEnabled)
            return;
        _dispatcher.RegisterEntity(npc);
        _npcCapabilities[npc] = _tools.GetAvailableToolNames(npc);
    }

    private void RefreshNpcCapabilitiesIfNeeded()
    {
        if (Time.unscaledTime < _nextCapabilityCheckAt)
            return;
        _nextCapabilityCheckAt = Time.unscaledTime + 0.5f;

        foreach (NpcEntity npc in new List<NpcEntity>(_npcCapabilities.Keys))
        {
            if (npc == null || !npc.isActiveAndEnabled)
            {
                _npcCapabilities.Remove(npc);
                continue;
            }

            List<string> current = _tools.GetAvailableToolNames(npc);
            List<string> previous = _npcCapabilities[npc];
            if (current.Count == previous.Count)
            {
                bool unchanged = true;
                for (int i = 0; i < current.Count; i++)
                {
                    if (!string.Equals(current[i], previous[i], StringComparison.Ordinal))
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged)
                    continue;
            }

            _npcCapabilities[npc] = current;
            _dispatcher.NotifyEntityCapabilitiesChanged(npc);
        }
    }

    public void OnPlayerInteractWithNpc(string npcId)
    {
        if (_contentReady && !_saveBusy && !_restoreFailed && !string.IsNullOrWhiteSpace(npcId))
            _activeNpcId = npcId;
    }

    public void SubmitPlayerInput(string text)
    {
        if (!_contentReady) return;
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
        _a2a == null ? Task.CompletedTask : _a2a.CancelActiveTaskAsync(_appCts.Token);

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
        NpcEntity.RuntimeAvailabilityChanged -= OnNpcRuntimeAvailabilityChanged;
        _appCts.Cancel();
        (_runtimeTransport as IDisposable)?.Dispose();
        _a2a?.Dispose();
        _saveCoordinator?.Dispose();
        _uiContentCatalog?.Dispose();
        _contentProvider?.Dispose();
        _sendLock.Dispose();
        _appCts.Dispose();
    }
}
