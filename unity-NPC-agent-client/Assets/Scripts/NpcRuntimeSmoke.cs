using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Explicit command-line diagnostics, like ToolPackageSmoke. Normal startup never
// creates files or changes cache selection. All Unity work stays on the main thread.
public sealed class NpcRuntimeSmoke : MonoBehaviour
{
    private const string Flag = "-gameWithLlmNpcRuntimeSmoke";
    private static Cache _cache;
    private static string Root => Environment.GetEnvironmentVariable("NPC_SMOKE_OUTPUT_PATH") ??
        throw new InvalidOperationException("NPC_SMOKE_OUTPUT_PATH is required.");
    private static bool Requested => Environment.GetCommandLineArgs().Contains(Flag, StringComparer.Ordinal);
    private bool _failed;
    private string _phase = "bootstrap";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void SelectIsolatedCache()
    {
        if (!Requested) return;
        Directory.CreateDirectory(Root);
        string cachePath = Path.Combine(Root, "bundle-cache");
        Directory.CreateDirectory(cachePath);
        _cache = Caching.AddCache(cachePath);
        if (!_cache.valid) throw new InvalidOperationException("Smoke cache is invalid.");
        Caching.currentCacheForWriting = _cache;
        string npcCache = Path.Combine(Root, "npc-bundle-cache");
        if (Directory.Exists(npcCache)) Caching.AddCache(npcCache);
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void StartWhenRequested()
    {
        if (!Requested) return;
        var runner = new GameObject(nameof(NpcRuntimeSmoke)).AddComponent<NpcRuntimeSmoke>();
        DontDestroyOnLoad(runner.gameObject);
        runner.Run();
    }

    private void OnLog(string message, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        _failed = true;
        File.WriteAllText(Path.Combine(Root, "scene-error"), type.ToString());
    }

    private async void Run()
    {
        Application.logMessageReceived += OnLog;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            await Wait(() => AgentHostClient.Instance != null && AgentHostClient.Instance.IsContentReady, timeout.Token);
            var host = AgentHostClient.Instance;
            var services = NpcLibraryServices.Current;
            var catalog = await services.CatalogClient.FetchAsync(timeout.Token);
            if (!new HashSet<string>(catalog.Npcs.Select(n => n.NpcId)).IsSupersetOf(new[] { "Merchant", "Guide" }))
                throw new InvalidOperationException("Smoke requires Merchant and Guide.");
            var checks = new List<string>();
            var provider = host.ContentProvider;
            if (!UnityEngine.AI.NavMesh.SamplePosition(FindFirstObjectByType<PlayerMock>().transform.position, out var testHit, 2f, UnityEngine.AI.NavMesh.AllAreas))
                throw new InvalidOperationException("Smoke player is not on NavMesh.");
            Vector3 testPosition = testHit.position;
            if (Environment.GetCommandLineArgs().Contains("-npcSmokeCachedStartup", StringComparer.Ordinal))
            {
                foreach (var npc in catalog.Npcs)
                {
                    if (!services.Installer.TryGet(npc.NpcId, out var installed) || await services.Installer.IsCacheMissingAsync(npc.NpcId, timeout.Token))
                        throw new InvalidOperationException("Cached NPC is incomplete.");
                    await services.Installer.ValidateInstalledAsync(npc.NpcId, timeout.Token);
                    await services.Spawner.SpawnAsync(installed, testPosition, timeout.Token);
                }
                Debug.Log("NPC_CACHED_STARTUP_SUCCESS");
                Application.Quit(0);
                return;
            }
            // Bootstrap assets can remain leased. Put NPC-only bundles in a second
            // dedicated cache so physical eviction never touches those active bundles.
            string npcCachePath = Path.Combine(Root, "npc-bundle-cache");
            Directory.CreateDirectory(npcCachePath);
            _cache = Caching.GetCacheByPath(npcCachePath);
            if (!_cache.valid) _cache = Caching.AddCache(npcCachePath);
            Caching.currentCacheForWriting = _cache;
            var summary = catalog.Npcs.Single(n => n.NpcId == "Merchant");
            _phase = "initial-install";
            var manifest = await services.CatalogClient.FetchManifestAsync(summary, timeout.Token);
            await services.Installer.InstallAsync(summary, timeout.Token);
            await Task.Yield(); await Task.Yield();
            _phase = "physical-cache-repair";
            // Physically evict only this invocation's cache; never clear the user's global cache.
            if (!_cache.ClearCache()) throw new InvalidOperationException("Isolated cache eviction failed.");
            if (await provider.GetDownloadSizeAsync(manifest.DownloadAddresses, timeout.Token) <= 0)
                throw new InvalidOperationException("Physical cache eviction did not require download.");
            await services.Installer.InstallAsync(summary, timeout.Token);
            if (await provider.GetDownloadSizeAsync(manifest.DownloadAddresses, timeout.Token) != 0)
                throw new InvalidOperationException("Physical cache repair failed.");
            checks.Add("cache-repair");
            _phase = "resource-faults";

            // Real candidate assets are still loaded/instantiated for these faults.
            // Corruption is applied to the leased inactive instance, never source assets.
            var fault = new FaultProvider(provider) { MissingAddress = true };
            await MustFail(() => NpcPresentationPreparation.ValidateAsync(fault, manifest, timeout.Token), "NPC_RESOURCE_RESTART_REQUIRED");
            checks.Add("catalog-address-missing");
            fault.MissingAddress = false;
            fault.CorruptAnimator = true;
            await MustFail(() => NpcPresentationPreparation.ValidateAsync(fault, manifest, timeout.Token));
            fault.CorruptAnimator = false;
            fault.MissingDependency = true;
            await MustFail(() => NpcPresentationPreparation.ValidateAsync(fault, manifest, timeout.Token));
            fault.MissingDependency = false;
            checks.Add("resource-integrity");

            using (var spawner = new NpcSpawnController(fault))
            {
                fault.PauseInstantiate = true;
                services.Installer.TryGet("Merchant", out var record);
                var pending = spawner.SpawnAsync(record, testPosition, timeout.Token);
                await Wait(() => fault.InstantiateEntered || pending.IsCompleted, timeout.Token);
                if (!fault.InstantiateEntered) await pending;
                await spawner.CancelPendingAsync();
                try { await pending; throw new InvalidOperationException("Spawn was not cancelled."); }
                catch (OperationCanceledException) { }
                if (spawner.IsSpawned("Merchant") || spawner.IsSpawning("Merchant"))
                    throw new InvalidOperationException("Cancelled spawn remained registered.");
                fault.PauseInstantiate = false;
            }
            checks.Add("scene-cancel");
            _phase = "spawn-current-catalog";
            foreach (var npc in catalog.Npcs)
            {
                var record = await services.Installer.InstallAsync(npc, timeout.Token);
                var entity = await services.Spawner.SpawnAsync(record, testPosition, timeout.Token);
                if (entity.ContentBinding.EntityId != npc.NpcId || !entity.GetComponent<CharacterVisualController>().IsRemoteVisualActive)
                    throw new InvalidOperationException("NPC visual/binding activation failed.");
            }
            checks.Add("dynamic-download");
            checks.Add("animation-driver");
            var saves = FindFirstObjectByType<PlayerMock>().SaveService;
            if (Environment.GetCommandLineArgs().Contains("-npcSmokeRestorePrevious", StringComparer.Ordinal))
            {
                var previous = saves.Load(File.ReadAllText(Path.Combine(Root, "previous-save-id")));
                foreach (var npc in previous.NpcContents) services.Spawner.Despawn(npc.EntityId);
                await Task.Yield();
                await services.PrepareRestoreAsync(previous, timeout.Token);
                saves.Apply(previous);
                if (!JToken.DeepEquals(JArray.FromObject(previous.Inventories), JArray.FromObject(saves.CaptureCurrentState().Inventories)))
                    throw new InvalidOperationException("Previous presentation inventory was not restored.");
                await host.PublishManifestAndWaitAsync(timeout.Token);
                File.WriteAllText(Path.Combine(Root, "previous-world-restored"), previous.SaveId);
                checks.Add("previous-presentation-world-restore");
            }
            var visuals = new JObject();
            foreach (var npc in catalog.Npcs)
            {
                var entity = FindObjectsByType<NpcEntity>(FindObjectsSortMode.None).Single(n => n.npcId == npc.NpcId);
                var mesh = entity.GetComponentInChildren<MeshFilter>();
                var animator = entity.GetComponentInChildren<Animator>();
                var scale = mesh.transform.localScale;
                visuals[npc.NpcId] = new JObject { ["displayName"] = npc.DisplayName,
                    ["mesh"] = mesh.sharedMesh.name, ["scale"] = new JObject { ["x"] = scale.x, ["y"] = scale.y, ["z"] = scale.z },
                    ["clipLength"] = animator.runtimeAnimatorController.animationClips[0].length };
            }
            File.WriteAllText(Path.Combine(Root, "player-checks.json"), new JObject {
                ["checks"] = JArray.FromObject(checks), ["visuals"] = visuals, ["cache"] = "isolated physical cache",
                ["faults"] = "candidate assets with injected missing address/dependency and corrupted instance" }.ToString());
            File.WriteAllText(Path.Combine(Root, "ready"), host.unityInstanceId);
            bool saved = false, restored = false;
            while (!File.Exists(Path.Combine(Root, "stop")))
            {
                timeout.Token.ThrowIfCancellationRequested();
                var merchant = FindObjectsByType<NpcEntity>(FindObjectsSortMode.None).FirstOrDefault(n => n.npcId == "Merchant");
                if (merchant != null)
                {
                    var state = (JObject)merchant.CreateRuntimeStateData();
                    var animator = merchant.GetComponentInChildren<Animator>();
                    state["animation"] = new JObject { ["initialized"] = animator != null && animator.isInitialized,
                        ["moving"] = animator != null && animator.isInitialized && animator.GetBool("Moving") };
                    File.WriteAllText(Path.Combine(Root, "state.json"), state.ToString());
                }
                if (!saved && File.Exists(Path.Combine(Root, "save-world")))
                {
                    var file = saves.Create("NPC current Player smoke");
                    File.WriteAllText(Path.Combine(Root, "world-contract.json"), new JObject {
                        ["entities"] = JArray.FromObject(file.Entities), ["inventories"] = JArray.FromObject(file.Inventories),
                        ["version"] = file.Version, ["npcContents"] = JArray.FromObject(file.NpcContents),
                        ["merchantPosition"] = JObject.FromObject(file.Entities.Single(e => e.EntityId == "Merchant").Position) }.ToString());
                    File.WriteAllText(Path.Combine(Root, "world-saved"), file.SaveId);
                    saved = true;
                }
                if (!restored && File.Exists(Path.Combine(Root, "restore-world")))
                {
                    var file = saves.Load(File.ReadAllText(Path.Combine(Root, "world-saved")));
                    foreach (var npc in file.NpcContents) services.Spawner.Despawn(npc.EntityId);
                    await Task.Yield();
                    await services.PrepareRestoreAsync(file, timeout.Token);
                    saves.Apply(file);
                    await host.PublishManifestAndWaitAsync(timeout.Token);
                    File.WriteAllText(Path.Combine(Root, "world-restored"), "ok");
                    restored = true;
                }
                await Task.Yield();
            }
            if (_failed || !restored) throw new InvalidOperationException("Player smoke incomplete or Console errors detected.");
            Debug.Log("NPC_RUNTIME_PLAYER_SUCCESS");
            Application.Quit(0);
        }
        catch (Exception ex)
        {
            // Diagnostic failures expose only exception type, never network content or credentials.
            string code = ex.Message.Contains("valid Avatar") ? "avatar-invalid" :
                ex.Message.Contains("RuntimeAnimatorController") ? "controller-invalid" :
                ex.Message.StartsWith("standard-locomotion requires", StringComparison.Ordinal) ? "animator-parameters-invalid" :
                ex.Message.Contains("Missing Script") ? "missing-script" :
                ex.Message.Contains("authoritative") ? "authoritative-component" : "unclassified";
            Debug.LogError("NPC_RUNTIME_PLAYER_FAILED: " + ex.GetType().Name + " phase=" + _phase + " code=" + code);
            Application.Quit(1);
        }
        finally { Application.logMessageReceived -= OnLog; }
    }

    private static async Task Wait(Func<bool> condition, CancellationToken token)
    { while (!condition()) { token.ThrowIfCancellationRequested(); await Task.Yield(); } }
    private static async Task MustFail(Func<Task> action, string code = null)
    {
        try { await action(); }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        { if (code != null && ex.Message != code) throw; return; }
        throw new InvalidOperationException("Expected resource failure was accepted.");
    }

    private sealed class FaultProvider : IContentAssetProvider
    {
        private readonly IContentAssetProvider _real;
        public bool MissingAddress, MissingDependency, CorruptAnimator, PauseInstantiate, InstantiateEntered;
        public FaultProvider(IContentAssetProvider real) { _real = real; }
        public bool IsInitialized => _real.IsInitialized;
        public Task InitializeAsync(CancellationToken t) => _real.InitializeAsync(t);
        public Task<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(CancellationToken t) => _real.CheckForCatalogUpdatesAsync(t);
        public Task UpdateCatalogsAsync(IReadOnlyList<string> ids, CancellationToken t) => _real.UpdateCatalogsAsync(ids, t);
        public Task<bool> AddressExistsAsync(string key, Type type, CancellationToken t) => MissingAddress ? Task.FromResult(false) : _real.AddressExistsAsync(key, type, t);
        public Task<long> GetDownloadSizeAsync(IReadOnlyList<string> keys, CancellationToken t) => _real.GetDownloadSizeAsync(keys, t);
        public Task DownloadDependenciesAsync(IReadOnlyList<string> keys, IProgress<ContentDownloadProgress> p, CancellationToken t) => _real.DownloadDependenciesAsync(keys, p, t);
        public Task<ContentAssetLease<T>> LoadAssetAsync<T>(object key, CancellationToken t) where T : UnityEngine.Object =>
            MissingDependency ? Task.FromException<ContentAssetLease<T>>(new IOException("Injected missing dependency")) : _real.LoadAssetAsync<T>(key, t);
        public async Task<ContentInstanceLease> InstantiateAsync(object key, Transform parent, CancellationToken t)
        {
            InstantiateEntered = true;
            while (PauseInstantiate) { t.ThrowIfCancellationRequested(); await Task.Yield(); }
            var lease = await _real.InstantiateAsync(key, parent, t);
            if (CorruptAnimator)
                foreach (var animator in lease.Instance.GetComponentsInChildren<Animator>(true)) animator.runtimeAnimatorController = null;
            return lease;
        }
        public void Dispose() { }
    }
}
