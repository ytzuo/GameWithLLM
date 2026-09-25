using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

public interface ISceneTransitionParticipant
{
    Task BeginSceneTransitionAsync(Scene outgoingScene, CancellationToken cancellationToken);
    Task CompleteSceneTransitionAsync(
        Scene incomingScene,
        string sceneId,
        CancellationToken cancellationToken);
    Task RollbackSceneTransitionAsync(Scene retainedScene, CancellationToken cancellationToken);
}

// A6 场景唯一入口。Bootstrap Scene 始终保留；远端 Scene 的 Addressables handle
// 只由此组件持有，并只通过 provider 的 Load/UnloadSceneAsync 成对管理。
public sealed class RemoteSceneCoordinator : MonoBehaviour
{
    public const string WarehouseAddress = "scene/warehouse/main";
    public const string WarehouseSceneId = "warehouse";

    [SerializeField] private string initialSceneAddress = WarehouseAddress;
    [SerializeField] private string initialSceneId = WarehouseSceneId;
    [SerializeField] private bool loadInitialScene = true;

    private readonly SemaphoreSlim _transitionGate = new SemaphoreSlim(1, 1);
    private IContentSceneProvider _content;
    private ISceneTransitionParticipant _participant;
    private ContentSceneLease _activeLease;
    private Scene _bootstrapScene;
    private Scene _activeContentScene;
    private CancellationToken _lifetimeToken;
    private bool _initialized;

    public bool HasActiveRemoteScene => _activeLease != null && !_activeLease.IsReleased;
    public string ActiveSceneId { get; private set; }

    public void Initialize(
        IContentSceneProvider content,
        ISceneTransitionParticipant participant,
        CancellationToken lifetimeToken)
    {
        if (_initialized)
            throw new InvalidOperationException("RemoteSceneCoordinator is already initialized.");
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _participant = participant ?? throw new ArgumentNullException(nameof(participant));
        _lifetimeToken = lifetimeToken;
        _bootstrapScene = gameObject.scene;
        if (!_bootstrapScene.IsValid() || !_bootstrapScene.isLoaded)
            throw new InvalidOperationException("RemoteSceneCoordinator must belong to a loaded Bootstrap Scene.");
        _initialized = true;
    }

    public Task LoadInitialSceneAsync(CancellationToken cancellationToken) =>
        !loadInitialScene || string.IsNullOrWhiteSpace(initialSceneAddress)
            ? Task.CompletedTask
            : SwitchAsync(initialSceneAddress, initialSceneId, cancellationToken);

    public async Task SwitchAsync(
        string address,
        string sceneId,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Remote scene address is required.", nameof(address));
        if (string.IsNullOrWhiteSpace(sceneId))
            throw new ArgumentException("Stable sceneId is required.", nameof(sceneId));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeToken);
        CancellationToken token = linked.Token;
        await _transitionGate.WaitAsync(token);
        ContentSceneLease candidate = null;
        Scene retained = _activeContentScene;
        bool transitionStarted = false;
        try
        {
            if (HasActiveRemoteScene && string.Equals(ActiveSceneId, sceneId, StringComparison.Ordinal))
                return;

            await _participant.BeginSceneTransitionAsync(retained, token);
            transitionStarted = true;
            candidate = await _content.LoadSceneAsync(
                address,
                LoadSceneMode.Additive,
                false,
                token);
            await AwaitActivationAsync(candidate.Scene.ActivateAsync(), token);
            Scene incoming = candidate.Scene.Scene;
            ValidateRemoteSceneBoundary(incoming);
            if (!SceneManager.SetActiveScene(incoming))
                throw new InvalidOperationException($"Unable to activate remote scene '{sceneId}'.");

            string nextSceneId = sceneId.Trim();
            await _participant.CompleteSceneTransitionAsync(incoming, nextSceneId, token);
            transitionStarted = false;
            ContentSceneLease previous = _activeLease;
            _activeLease = candidate;
            _activeContentScene = incoming;
            ActiveSceneId = nextSceneId;
            candidate = null;

            if (previous != null)
            {
                try { await _content.UnloadSceneAsync(previous, token); }
                catch (Exception unloadError)
                {
                    Debug.LogWarning(
                        $"[Content] Previous remote scene release failed: {unloadError.Message}");
                }
            }
            Debug.Log($"[Content] Remote scene '{ActiveSceneId}' activated from '{address}'.");
        }
        catch
        {
            if (candidate != null)
            {
                try { await _content.UnloadSceneAsync(candidate, CancellationToken.None); }
                catch (Exception unloadError)
                {
                    Debug.LogWarning($"[Content] Failed to release rejected scene: {unloadError.Message}");
                }
            }
            Scene fallback = retained.IsValid() && retained.isLoaded ? retained : _bootstrapScene;
            if (fallback.IsValid() && fallback.isLoaded)
                SceneManager.SetActiveScene(fallback);
            if (transitionStarted)
                await _participant.RollbackSceneTransitionAsync(retained, CancellationToken.None);
            throw;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task ReturnToBootstrapAsync(CancellationToken cancellationToken)
    {
        RequireInitialized();
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (!HasActiveRemoteScene)
                return;
            Scene outgoing = _activeContentScene;
            await _participant.BeginSceneTransitionAsync(outgoing, cancellationToken);
            SceneManager.SetActiveScene(_bootstrapScene);
            ContentSceneLease lease = _activeLease;
            _activeLease = null;
            _activeContentScene = default;
            ActiveSceneId = null;
            await _content.UnloadSceneAsync(lease, cancellationToken);
            await _participant.CompleteSceneTransitionAsync(
                _bootstrapScene,
                _bootstrapScene.name,
                cancellationToken);
        }
        catch
        {
            await _participant.RollbackSceneTransitionAsync(_activeContentScene, CancellationToken.None);
            throw;
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public static void ValidateRemoteSceneBoundary(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded)
            throw new InvalidOperationException("Remote scene is not loaded.");
        Type[] forbiddenTypes =
        {
            typeof(RemoteSceneCoordinator),
            typeof(ContentBootstrapOverlay),
            typeof(ToolsRegistry),
            typeof(CommandDispatcher)
        };
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Type type in forbiddenTypes)
            {
                if (root.GetComponentInChildren(type, true) != null)
                    throw new InvalidOperationException(
                        $"Remote scene '{scene.name}' contains persistent component '{type.Name}'.");
            }
            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string typeName = behaviour == null ? "MissingScript" : behaviour.GetType().Name;
                if (typeName == "AgentHostClient" || typeName == "UIManager")
                    throw new InvalidOperationException(
                        $"Remote scene '{scene.name}' contains persistent component '{typeName}'.");
            }
        }
    }

    private static async Task AwaitActivationAsync(AsyncOperation operation, CancellationToken token)
    {
        if (operation == null)
            throw new InvalidOperationException("Remote scene activation did not start.");
        while (!operation.isDone)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private void RequireInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("RemoteSceneCoordinator has not been initialized.");
    }

    private void OnDestroy()
    {
        _activeLease?.Dispose();
        _activeLease = null;
        _transitionGate.Dispose();
    }
}
