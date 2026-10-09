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
    public string ActiveCatalogContentVersion { get; }

    public NpcLibraryServices(IContentAssetProvider provider, string activeCatalogContentVersion)
    {
        string root = Path.Combine(Application.persistentDataPath, "NpcContent");
        CatalogClient = new RemoteNpcCatalogClient(
            RemoteNpcContentRootResolver.Resolve(), root, TimeSpan.FromSeconds(15));
        Spawner = new NpcSpawnController(provider);
        Installer = new NpcContentInstaller(provider, CatalogClient, root, Spawner.IsSpawned);
        ActiveCatalogContentVersion = activeCatalogContentVersion ?? string.Empty;
        Current = this;
    }

    public void Dispose()
    {
        Installer.Cancel();
        CatalogClient.Dispose();
        Spawner.Dispose();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    public async Task PrepareRestoreAsync(
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
            if (!string.Equals(installed.ContentVersion, requirement.ContentVersion, StringComparison.Ordinal) ||
                !string.Equals(installed.ManifestSha256, requirement.ManifestSha256, StringComparison.Ordinal))
                throw new InvalidOperationException($"NPC_CONTENT_VERSION_CONFLICT:{requirement.EntityId}");
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
            Spawner.DespawnRemoteExcept(new HashSet<string>(
                requirements.Select(item => item.EntityId), StringComparer.Ordinal));
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
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (SaveGameNpcContentState item in requirements ?? Array.Empty<SaveGameNpcContentState>())
        {
            if (item == null || string.IsNullOrWhiteSpace(item.EntityId) ||
                !IsDigits(item.ContentVersion) ||
                !IsLowerHexSha256(item.ManifestSha256) || !ids.Add(item.EntityId))
                throw new InvalidDataException("NPC_CONTENT_SAVE_BINDING_INVALID");
        }
    }

    private static bool IsDigits(string value) =>
        !string.IsNullOrEmpty(value) && value.All(character => character >= '0' && character <= '9');

    private static bool IsLowerHexSha256(string value) =>
        value != null && value.Length == 64 &&
        value.All(character => (character >= '0' && character <= '9') ||
                               (character >= 'a' && character <= 'f'));
}

public sealed class NpcSpawnController : IDisposable
{
    private readonly IContentAssetProvider _provider;
    private readonly Dictionary<string, GameObject> _instances =
        new Dictionary<string, GameObject>(StringComparer.Ordinal);
    private readonly HashSet<string> _spawning = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<NpcEntity>> _spawnTasks =
        new Dictionary<string, Task<NpcEntity>>(StringComparer.Ordinal);
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
    public bool IsSpawning(string npcId) => _spawning.Contains(npcId);

    public Task<NpcEntity> SpawnAsync(InstalledNpcRecord record, CancellationToken token)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        if (_spawnTasks.TryGetValue(record.NpcId, out Task<NpcEntity> active)) return active;
        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _sceneCts.Token);
        Task<NpcEntity> task = SpawnCoreAsync(record, linked.Token);
        _spawnTasks[record.NpcId] = task;
        _ = ObserveSpawnAsync(record.NpcId, task, linked);
        return task;
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
        try
        {
            PlayerMock player = UnityEngine.Object.FindFirstObjectByType<PlayerMock>(
                FindObjectsInactive.Exclude);
            if (player == null)
                throw new InvalidOperationException("NPC_SPAWN_ACTIVE_PLAYER_MISSING");
            RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(record.ManifestJson);
            if (manifest.NpcId != record.NpcId || manifest.ContentVersion != record.ContentVersion)
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
            entity.BindContent(new NpcContentBinding(record.NpcId, record.ContentVersion, record.ManifestSha256));

            var visualRoot = new GameObject("VisualRoot").transform;
            visualRoot.SetParent(root.transform, false);
            var visuals = root.AddComponent<CharacterVisualController>();
            visuals.ConfigureDynamic(record.NpcId, visualRoot);
            Animator animator = await visuals.LoadStrictAsync(
                _provider, manifest.PrefabAddress, manifest.AnimatorControllerAddress, token);

            ContentAssetLease<TextAsset> dllLease = await _provider.LoadAssetAsync<TextAsset>(
                manifest.AnimationScriptAddress, token);
            INpcAnimationDriver driver;
            try
            {
                byte[] dll = dllLease.Asset?.bytes;
                if (dll == null || dll.LongLength != manifest.AnimationLength ||
                    !string.Equals(RemoteNpcContract.ComputeSha256(dll), manifest.AnimationSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("NPC animation assembly integrity check failed.");
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                    x => string.Equals(x.GetName().Name, manifest.AnimationAssemblyName, StringComparison.Ordinal)) ??
                    Assembly.Load(dll);
                if (!string.Equals(assembly.GetName().Name, manifest.AnimationAssemblyName, StringComparison.Ordinal))
                    throw new InvalidDataException("NPC animation assembly name mismatch.");
                Type type = assembly.GetType(manifest.AnimationEntryType, true, false);
                if (type.IsAbstract || !typeof(INpcAnimationDriver).IsAssignableFrom(type) ||
                    type.GetConstructor(Type.EmptyTypes) == null)
                    throw new InvalidDataException("NPC animation entry contract is invalid.");
                driver = (INpcAnimationDriver)Activator.CreateInstance(type);
                driver.Bind(animator);
            }
            catch { dllLease.Dispose(); throw; }

            var driverHost = root.AddComponent<NpcAnimationDriverHost>();
            driverHost.Initialize(driver, nav, dllLease);
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
            if (root != null)
            {
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
        if (binding == null ||
            !string.Equals(binding.ContentVersion, requirement.ContentVersion, StringComparison.Ordinal) ||
            !string.Equals(binding.ManifestSha256, requirement.ManifestSha256, StringComparison.Ordinal))
            throw new InvalidOperationException($"NPC_CONTENT_VERSION_CONFLICT:{requirement.EntityId}");
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
        instance.SetActive(false);
        UnityEngine.Object.Destroy(instance);
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
                UnityEngine.Object.Destroy(instance);
            }
        _instances.Clear();
    }
}

internal sealed class NpcAnimationDriverHost : MonoBehaviour
{
    private INpcAnimationDriver _driver;
    private NavMeshAgent _agent;
    private IDisposable _assemblyLease;

    public void Initialize(INpcAnimationDriver driver, NavMeshAgent agent, IDisposable assemblyLease)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _assemblyLease = assemblyLease ?? throw new ArgumentNullException(nameof(assemblyLease));
    }

    private void Update()
    {
        if (_driver != null)
            _driver.Tick(Time.deltaTime, _agent != null && _agent.enabled ? _agent.velocity.magnitude : 0f);
    }

    private void OnDestroy()
    {
        try { _driver?.Dispose(); }
        finally { _driver = null; _assemblyLease?.Dispose(); _assemblyLease = null; }
    }
}
