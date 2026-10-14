using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;
using UnityEngine.AI;

public sealed class NpcLibraryServices : IDisposable
{
    public static NpcLibraryServices Current { get; private set; }
    public RemoteNpcCatalogClient CatalogClient { get; }
    public NpcContentInstaller Installer { get; }
    public NpcSpawnController Spawner { get; }

    public NpcLibraryServices(IContentAssetProvider provider)
    {
        string root = Path.Combine(Application.persistentDataPath, "NpcContent");
        CatalogClient = new RemoteNpcCatalogClient(
            RemoteNpcContentRootResolver.Resolve(), root, TimeSpan.FromSeconds(15));
        Spawner = new NpcSpawnController(provider);
        Installer = new NpcContentInstaller(provider, CatalogClient, root, name => Spawner.IsSpawned(name) || Spawner.IsSpawning(name));
        Current = this;
    }

    public void Dispose()
    {
        Installer.Cancel();
        CatalogClient.Dispose();
        Spawner.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    public async Task PrepareRestoreAsync(IReadOnlyList<SaveGameNpcContentState> requirements, CancellationToken token)
    {
        using var preparation = await BeginRestoreAsync(requirements, token);
        preparation.Commit();
    }

    public async Task<NpcRestorePreparation> BeginRestoreAsync(
        IReadOnlyList<SaveGameNpcContentState> requirements,
        CancellationToken token)
    {
        requirements ??= Array.Empty<SaveGameNpcContentState>();
        ValidateRestoreRequirements(requirements);

        // Finish every non-mutating prerequisite before creating any entity. Missing
        // content therefore cannot leave a partially restored dynamic world.
        foreach (SaveGameNpcContentState requirement in requirements)
        {
            if (!Installer.TryGet(requirement.EntityId, out InstalledNpcRecord installed))
                throw new InvalidOperationException($"NPC_CONTENT_NOT_INSTALLED:{requirement.EntityId}");
            if (await Installer.IsCacheMissingAsync(requirement.EntityId, token))
                throw new InvalidOperationException($"NPC_CONTENT_CACHE_MISSING:{requirement.EntityId}");
            Spawner.ValidateExistingBinding(requirement);
        }

        var created = new List<string>();
        try
        {
            foreach (SaveGameNpcContentState requirement in requirements)
            {
                if (Spawner.IsSpawned(requirement.EntityId)) continue;
                Installer.TryGet(requirement.EntityId, out InstalledNpcRecord installed);
                await Spawner.SpawnAsync(installed, token);
                created.Add(requirement.EntityId);
            }
            token.ThrowIfCancellationRequested();
            return new NpcRestorePreparation(Spawner, created, Spawner.SuspendRemoteExcept(
                new HashSet<string>(requirements.Select(item => item.EntityId), StringComparer.OrdinalIgnoreCase)));
        }
        catch
        {
            foreach (string npcId in created) Spawner.Despawn(npcId);
            throw;
        }
    }

    public async Task BeginSceneTransitionAsync()
    {
        await Installer.CancelAndWaitAsync();
        await Spawner.CancelPendingAsync();
    }

    public static void ValidateRestoreRequirements(IReadOnlyList<SaveGameNpcContentState> requirements)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SaveGameNpcContentState item in requirements ?? Array.Empty<SaveGameNpcContentState>())
        {
            if (item == null || string.IsNullOrWhiteSpace(item.EntityId) ||
                RemoteNpcContract.ValidateName(item.EntityId) != item.EntityId || !ids.Add(item.EntityId))
                throw new InvalidDataException("NPC_CONTENT_SAVE_BINDING_INVALID");
        }
    }

}

public sealed class NpcSpawnController : IDisposable
{
    private readonly IContentAssetProvider _provider;
    private readonly Dictionary<string, GameObject> _instances =
        new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _spawning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<NpcEntity>> _spawnTasks =
        new Dictionary<string, Task<NpcEntity>>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _sceneCts = new CancellationTokenSource();

    public NpcSpawnController(IContentAssetProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        foreach (NpcEntity npc in UnityEngine.Object.FindObjectsByType<NpcEntity>(
                     FindObjectsInactive.Exclude,
                     FindObjectsSortMode.None))
            if (npc != null && !string.IsNullOrWhiteSpace(npc.npcId)) _instances[npc.npcId] = npc.gameObject;
    }

    public bool IsSpawned(string npcId)
    {
        if (string.IsNullOrWhiteSpace(npcId)) return false;
        if (_instances.TryGetValue(npcId, out GameObject instance))
        {
            if (instance != null) return true;
            _instances.Remove(npcId);
        }
        NpcEntity existing = UnityEngine.Object.FindObjectsByType<NpcEntity>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(value => string.Equals(value.npcId, npcId, StringComparison.Ordinal));
        if (existing == null) return false;
        _instances[npcId] = existing.gameObject;
        return true;
    }
    public bool IsSpawning(string npcId) => _spawning.Contains(npcId) || _spawnTasks.ContainsKey(npcId);

    public Task<NpcEntity> SpawnAsync(InstalledNpcRecord record, CancellationToken token)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        token.ThrowIfCancellationRequested();
        if (_spawnTasks.TryGetValue(record.NpcId, out Task<NpcEntity> active)) return active;
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _sceneCts.Token);
        Task<NpcEntity> task = SpawnDeferredAsync(record, linked.Token);
        _spawnTasks[record.NpcId] = task;
        _ = ObserveSpawnAsync(record.NpcId, task, linked);
        return task;
    }

    private async Task<NpcEntity> SpawnDeferredAsync(InstalledNpcRecord record, CancellationToken token)
    {
        await Task.Yield();
        token.ThrowIfCancellationRequested();
        return await SpawnCoreAsync(record, token);
    }

    private async Task<NpcEntity> SpawnCoreAsync(InstalledNpcRecord record, CancellationToken token)
    {
        NpcEntity existing = UnityEngine.Object.FindObjectsByType<NpcEntity>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None)
            .FirstOrDefault(npc => string.Equals(npc.npcId, record.NpcId, StringComparison.Ordinal));
        if (existing != null)
        {
            _instances[record.NpcId] = existing.gameObject;
            return existing;
        }
        if (IsSpawned(record.NpcId)) return _instances[record.NpcId].GetComponent<NpcEntity>();
        if (!_spawning.Add(record.NpcId)) throw new InvalidOperationException("NPC_SPAWN_BUSY");

        GameObject root = null;
        NpcPresentationPreparation prepared = null;
        try
        {
            PlayerMock player = UnityEngine.Object.FindFirstObjectByType<PlayerMock>(
                FindObjectsInactive.Exclude);
            if (player == null)
                throw new InvalidOperationException("NPC_SPAWN_ACTIVE_PLAYER_MISSING");
            RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(record.ManifestJson);
            if (manifest.NpcId != record.NpcId)
                throw new InvalidDataException("Installed NPC identity is inconsistent.");
            if (!NavMesh.SamplePosition(Vector3.zero, out NavMeshHit originHit, 0.05f, NavMesh.AllAreas) ||
                Vector3.Distance(originHit.position, Vector3.zero) > 0.01f)
                throw new InvalidOperationException("NPC_SPAWN_ORIGIN_NOT_ON_NAVMESH");

            root = new GameObject($"Remote NPC - {record.DisplayName}");
            root.SetActive(false);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var nav = root.AddComponent<NavMeshAgent>();
            nav.speed = 3.5f; nav.angularSpeed = 240f; nav.acceleration = 8f;
            var inventory = root.AddComponent<InventoryComponent>();
            inventory.ConfigureContainerId($"npc:{record.NpcId}.inventory");
            var entity = root.AddComponent<NpcEntity>();
            entity.npcId = record.NpcId;
            entity.BindContent(new NpcContentBinding(record.NpcId));

            var visualRoot = new GameObject("VisualRoot").transform;
            visualRoot.SetParent(root.transform, false);
            var visuals = root.AddComponent<CharacterVisualController>();
            visuals.ConfigureDynamic(record.NpcId, visualRoot);
            prepared = await NpcPresentationPreparation.PrepareAsync(
                _provider, manifest, visualRoot, token);
            prepared.Instance.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            prepared.Instance.transform.localScale = Vector3.one;
            visuals.AttachPreparedPresentation(prepared);
            var driverHost = root.AddComponent<NpcAnimationDriverHost>();
            driverHost.Initialize(prepared.Driver, nav, entity, prepared);
            prepared = null;
            token.ThrowIfCancellationRequested();
            root.SetActive(true);
            if (!nav.isOnNavMesh) throw new InvalidOperationException("NPC_SPAWN_ORIGIN_NOT_ON_NAVMESH");
            inventory.SetDisplayName(record.DisplayName);
            player.npcEntities.Add(entity);
            _instances.Add(record.NpcId, root);
            return entity;
        }
        catch
        {
            prepared?.Dispose();
            if (root != null)
            {
                root.GetComponent<NpcAnimationDriverHost>()?.Release();
                root.GetComponent<CharacterVisualController>()?.ReleaseVisual();
                root.SetActive(false);
                UnityEngine.Object.Destroy(root);
            }
            throw;
        }
        finally { _spawning.Remove(record.NpcId); }
    }

    private async Task ObserveSpawnAsync(
        string npcId,
        Task<NpcEntity> task,
        CancellationTokenSource linked)
    {
        try { await task; }
        catch { }
        finally
        {
            linked.Dispose();
            if (_spawnTasks.TryGetValue(npcId, out Task<NpcEntity> current) && ReferenceEquals(current, task))
                _spawnTasks.Remove(npcId);
        }
    }

    public async Task CancelPendingAsync()
    {
        CancellationTokenSource cancelled = _sceneCts;
        _sceneCts = new CancellationTokenSource();
        cancelled.Cancel();
        Task[] tasks = _spawnTasks.Values.Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks); }
            catch { }
        }
        cancelled.Dispose();
    }

    public void ValidateExistingBinding(SaveGameNpcContentState requirement)
    {
        NpcEntity entity = UnityEngine.Object.FindObjectsByType<NpcEntity>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None)
            .FirstOrDefault(value => string.Equals(value.npcId, requirement.EntityId, StringComparison.Ordinal));
        if (entity == null) return;
        NpcContentBinding binding = entity.ContentBinding;
        if (binding == null)
            throw new InvalidOperationException($"NPC_CONTENT_BINDING_INVALID:{requirement.EntityId}");
    }

    public void Despawn(string npcId)
    {
        if (!_instances.TryGetValue(npcId, out GameObject instance)) return;
        _instances.Remove(npcId);
        if (instance == null) return;
        NpcEntity entity = instance.GetComponent<NpcEntity>();
        foreach (PlayerMock player in UnityEngine.Object.FindObjectsByType<PlayerMock>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            player.npcEntities.Remove(entity);
        instance.GetComponent<NpcAnimationDriverHost>()?.Release();
        instance.SetActive(false);
        UnityEngine.Object.Destroy(instance);
    }

    public List<GameObject> SuspendRemoteExcept(ISet<string> retainedNpcIds)
    {
        var suspended = new List<GameObject>();
        foreach (NpcEntity entity in UnityEngine.Object.FindObjectsByType<NpcEntity>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (entity?.ContentBinding == null || retainedNpcIds.Contains(entity.npcId)) continue;
            _instances[entity.npcId] = entity.gameObject;
            suspended.Add(entity.gameObject);
            foreach (PlayerMock player in UnityEngine.Object.FindObjectsByType<PlayerMock>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                player.npcEntities.Remove(entity);
            entity.gameObject.SetActive(false);
        }
        return suspended;
    }

    public void DespawnRemoteExcept(ISet<string> retainedNpcIds)
    {
        foreach (NpcEntity entity in UnityEngine.Object.FindObjectsByType<NpcEntity>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (entity?.ContentBinding == null || retainedNpcIds.Contains(entity.npcId)) continue;
            _instances[entity.npcId] = entity.gameObject;
            Despawn(entity.npcId);
        }
    }

    public void Dispose()
    {
        _sceneCts.Cancel();
        _sceneCts.Dispose();
        _spawning.Clear();
        foreach (GameObject instance in _instances.Values)
            if (instance != null && instance.GetComponent<NpcEntity>()?.ContentBinding != null)
            {
                NpcEntity entity = instance.GetComponent<NpcEntity>();
                foreach (PlayerMock player in UnityEngine.Object.FindObjectsByType<PlayerMock>(
                             FindObjectsInactive.Include,
                             FindObjectsSortMode.None))
                    player.npcEntities.Remove(entity);
                instance.GetComponent<NpcAnimationDriverHost>()?.Release();
                instance.SetActive(false);
                UnityEngine.Object.Destroy(instance);
            }
        _instances.Clear();
    }
}

// Keep displaced entities and their leases until both world and Go restore succeed.
public sealed class NpcRestorePreparation : IDisposable
{
    private readonly NpcSpawnController _spawner;
    private readonly List<string> _created;
    private readonly List<GameObject> _suspended;
    private bool _finished;
    internal NpcRestorePreparation(NpcSpawnController spawner, List<string> created, List<GameObject> suspended)
    { _spawner = spawner; _created = created; _suspended = suspended; }
    public void Commit()
    {
        if (_finished) return;
        _finished = true;
        foreach (GameObject instance in _suspended)
            if (instance != null) _spawner.Despawn(instance.GetComponent<NpcEntity>().npcId);
    }
    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        foreach (string name in _created) _spawner.Despawn(name);
        foreach (GameObject instance in _suspended)
        {
            if (instance == null) continue;
            var entity = instance.GetComponent<NpcEntity>();
            foreach (PlayerMock player in UnityEngine.Object.FindObjectsByType<PlayerMock>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (!player.npcEntities.Contains(entity)) player.npcEntities.Add(entity);
            instance.SetActive(true);
        }
    }
}

internal sealed class NpcAnimationDriverHost : MonoBehaviour
{
    private INpcAnimationDriver _driver;
    private NavMeshAgent _agent;
    private IDisposable _assemblyLease;

    public void Initialize(
        INpcAnimationDriver driver,
        NavMeshAgent agent,
        INpcAnimationEventSource eventSource,
        IDisposable assemblyLease)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _assemblyLease = assemblyLease;
        if (_driver is INpcEventDrivenAnimationDriver eventDriven)
            eventDriven.BindEventSource(eventSource ?? throw new ArgumentNullException(nameof(eventSource)));
    }

    private void Update()
    {
        if (_driver != null)
            _driver.Tick(Time.deltaTime, _agent != null && _agent.enabled ? _agent.velocity.magnitude : 0f);
    }

    public void Release()
    {
        // The preparation lease owns the driver as well as both resources.
        try { if (_assemblyLease == null) _driver?.Dispose(); }
        finally { _driver = null; _assemblyLease?.Dispose(); _assemblyLease = null; }
    }
    private void OnDestroy() => Release();
}
