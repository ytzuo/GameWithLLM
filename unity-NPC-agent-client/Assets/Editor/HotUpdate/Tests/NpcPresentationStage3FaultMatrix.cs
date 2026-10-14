using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Faults are injected at the existing provider boundary. Successful operations
// still use the real Addressables provider and SampleScene, never a test transport.
public static class NpcPresentationStage3FaultMatrix
{
    public static async Task RunAsync()
    {
        var services = NpcLibraryServices.Current;
        var host = UnityEngine.Object.FindFirstObjectByType<AgentHostClient>();
        var real = (ContentAssetProvider)Field(typeof(AgentHostClient), "_contentProvider").GetValue(host);
        var provider = new FaultProvider(real);
        var checks = new List<string>();
        int baseline = LeaseCount(real);
        Field(typeof(NpcContentInstaller), "_provider").SetValue(services.Installer, provider);
        Field(typeof(NpcSpawnController), "_provider").SetValue(services.Spawner, provider);
        try
        {
            var catalog = await services.CatalogClient.FetchAsync(CancellationToken.None);
            var merchant = catalog.Npcs.Single(value => value.NpcId == "Merchant");
            var guide = catalog.Npcs.Single(value => value.NpcId == "Guide");
            var record = await services.Installer.InstallAsync(merchant, CancellationToken.None);
            await services.Installer.InstallAsync(guide, CancellationToken.None);
            Check(LeaseCount(real) == baseline, "install-leases");

            provider.MissingAddress = true;
            await Fails(() => services.Installer.InstallAsync(merchant, CancellationToken.None), "NPC_RESOURCE_RESTART_REQUIRED");
            provider.MissingAddress = false;
            provider.FailLoad = true;
            await Fails(() => services.Installer.InstallAsync(merchant, CancellationToken.None));
            provider.FailLoad = false;
            Check(services.Installer.TryGet("Merchant", out var retained) && ReferenceEquals(record, retained), "retained-install");
            Check(LeaseCount(real) == baseline, "failed-install-leases");
            checks.Add("address-missing-restart-and-load-failure-retain-record");

            provider.PauseDownload = true;
            var installing = services.Installer.InstallAsync(merchant, CancellationToken.None);
            await Wait(() => provider.DownloadEntered);
            await Fails(() => services.Installer.InstallAsync(guide, CancellationToken.None), "NPC_INSTALL_BUSY");
            await services.Installer.CancelAndWaitAsync();
            await Cancelled(installing);
            Check(services.Installer.ActiveNpcId == null && LeaseCount(real) == baseline, "cancel-install-cleanup");
            provider.PauseDownload = false;
            checks.Add("single-download-and-cancel-no-commit");

            provider.CacheMissing = true;
            Check(await services.Installer.IsCacheMissingAsync("Merchant", CancellationToken.None), "evicted-cache");
            record = await services.Installer.InstallAsync(merchant, CancellationToken.None);
            Check(!await services.Installer.IsCacheMissingAsync("Merchant", CancellationToken.None), "cache-repaired");
            // No HTTP request occurs when preparing a last complete installation.
            await services.Installer.ValidateInstalledAsync("Merchant", CancellationToken.None);
            Check(LeaseCount(real) == baseline, "offline-prepare-leases");
            checks.Add("cache-eviction-repair-and-offline-installed-preparation");

            provider.PauseInstantiate = true;
            provider.InstantiateEntered = false;
            var spawning = services.Spawner.SpawnAsync(record, CancellationToken.None);
            Check(ReferenceEquals(spawning, services.Spawner.SpawnAsync(record, CancellationToken.None)), "unique-spawn-task");
            await Wait(() => provider.InstantiateEntered || spawning.IsCompleted);
            if (!provider.InstantiateEntered) await spawning;
            await services.BeginSceneTransitionAsync();
            await Cancelled(spawning);
            Check(!services.Spawner.IsSpawned("Merchant") && !services.Spawner.IsSpawning("Merchant"), "spawn-cancel-cleanup");
            Check(LeaseCount(real) == baseline, "cancel-spawn-leases");
            provider.PauseInstantiate = false;
            checks.Add("same-name-task-and-scene-transition-cancellation");

            var player = UnityEngine.Object.FindFirstObjectByType<PlayerMock>();
            var saves = (SaveGameService)Field(typeof(PlayerMock), "_saveGameService").GetValue(player);
            var original = saves.CaptureCurrentState();
            provider.FailLoad = true;
            await Fails(() => services.BeginRestoreAsync(new[] { Required("Merchant"), Required("Guide") }, CancellationToken.None));
            provider.FailLoad = false;
            Check(!services.Spawner.IsSpawned("Merchant") && LeaseCount(real) == baseline, "preflight-no-world-mutation");
            provider.FailGuideInstantiate = true;
            await Fails(() => services.BeginRestoreAsync(new[] { Required("Merchant"), Required("Guide") }, CancellationToken.None));
            provider.FailGuideInstantiate = false;
            Check(!services.Spawner.IsSpawned("Merchant") && !services.Spawner.IsSpawned("Guide"), "restore-created-rollback");
            Check(LeaseCount(real) == baseline, "restore-failure-leases");
            Check(JToken.DeepEquals(JArray.FromObject(original.Inventories), JArray.FromObject(saves.CaptureCurrentState().Inventories)), "inventory-unchanged");
            checks.Add("restore-preflight-and-second-spawn-failure-rollback");

            // A completed preparation remains rollbackable until Go restore commits.
            using (var preparation = await services.BeginRestoreAsync(new[] { Required("Merchant"), Required("Guide") }, CancellationToken.None))
                Check(services.Spawner.IsSpawned("Merchant") && services.Spawner.IsSpawned("Guide"), "prepared-world");
            Check(!services.Spawner.IsSpawned("Merchant") && !services.Spawner.IsSpawned("Guide"), "prepared-world-rollback");
            Check(LeaseCount(real) == baseline, "transaction-rollback-leases");
            checks.Add("uncommitted-restore-rollback");
            provider.SwapVisual = true;
            string previousDisplayName = record.DisplayName;
            record.DisplayName = "更新后的中文展示名";
            try
            {
                using var changed = await services.BeginRestoreAsync(new[] { Required("Merchant") }, CancellationToken.None);
                var entity = UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsSortMode.None).Single(value => value.npcId == "Merchant");
                Check(entity.name.Contains(record.DisplayName) && entity.ContentBinding.EntityId == "Merchant", "changed-presentation-identity");
                Check(entity.GetComponent<CharacterVisualController>().IsRemoteVisualActive, "prepared-visual-active");
            }
            finally { provider.SwapVisual = false; record.DisplayName = previousDisplayName; }
            Check(LeaseCount(real) == baseline, "changed-presentation-cleanup");
            checks.Add("changed-model-animation-and-display-name-restore");
            await Task.Yield();
            Check(!UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Any(value => value.ContentBinding != null), "no-orphan-entities");
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Artifacts/NpcPresentationStage3/fault-report.json"));
            File.WriteAllText(output, new JObject { ["passed"] = true, ["checks"] = JArray.FromObject(checks),
                ["provider"] = "real Addressables with injected boundary faults", ["leaseBaseline"] = baseline,
                ["leaseFinal"] = LeaseCount(real) }.ToString());
        }
        finally
        {
            provider.PauseDownload = provider.PauseInstantiate = false;
            Field(typeof(NpcContentInstaller), "_provider").SetValue(services.Installer, real);
            Field(typeof(NpcSpawnController), "_provider").SetValue(services.Spawner, real);
        }
    }
    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    private static int LeaseCount(ContentAssetProvider provider) => ((System.Collections.IEnumerable)Field(typeof(ContentAssetProvider), "_leases").GetValue(provider)).Cast<object>().Count();
    private static SaveGameNpcContentState Required(string name) => new SaveGameNpcContentState { EntityId = name };
    private static void Check(bool condition, string code) { if (!condition) throw new InvalidOperationException("Stage3 check failed: " + code); }
    private static async Task Wait(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition()) { Check(DateTime.UtcNow < deadline, "fault-timeout"); await Task.Yield(); }
    }
    private static async Task Fails(Func<Task> action, string code = null)
    {
        try { await action(); }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        { if (code != null) Check(ex.Message == code, "expected-error-" + code); return; }
        throw new InvalidOperationException("Stage3 expected failure did not occur.");
    }
    private static async Task Cancelled(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Stage3 expected cancellation did not occur.");
    }
    private sealed class FaultProvider : IContentAssetProvider
    {
        private readonly IContentAssetProvider _real;
        public bool MissingAddress, FailLoad, PauseDownload, DownloadEntered, CacheMissing;
        public bool PauseInstantiate, InstantiateEntered, FailGuideInstantiate, SwapVisual;
        private int _guideInstances;
        public FaultProvider(IContentAssetProvider real) { _real = real; }
        public bool IsInitialized => _real.IsInitialized;
        public Task InitializeAsync(CancellationToken token) => _real.InitializeAsync(token);
        public Task<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(CancellationToken token) => _real.CheckForCatalogUpdatesAsync(token);
        public Task UpdateCatalogsAsync(IReadOnlyList<string> ids, CancellationToken token) => _real.UpdateCatalogsAsync(ids, token);
        public Task<bool> AddressExistsAsync(string key, Type type, CancellationToken token) => MissingAddress ? Task.FromResult(false) : _real.AddressExistsAsync(key, type, token);
        public Task<long> GetDownloadSizeAsync(IReadOnlyList<string> keys, CancellationToken token) => CacheMissing ? Task.FromResult(512L) : _real.GetDownloadSizeAsync(keys, token);
        public async Task DownloadDependenciesAsync(IReadOnlyList<string> keys, IProgress<ContentDownloadProgress> progress, CancellationToken token)
        {
            DownloadEntered = true;
            while (PauseDownload) { token.ThrowIfCancellationRequested(); await Task.Yield(); }
            await _real.DownloadDependenciesAsync(keys, progress, token); CacheMissing = false;
        }
        public Task<ContentAssetLease<T>> LoadAssetAsync<T>(object key, CancellationToken token) where T : UnityEngine.Object =>
            FailLoad ? Task.FromException<ContentAssetLease<T>>(new IOException("Injected unavailable dependency")) : _real.LoadAssetAsync<T>(SwapVisual && (string)key == "npc/Merchant/visual" ? "npc/Guide/visual" : key, token);
        public async Task<ContentInstanceLease> InstantiateAsync(object key, Transform parent, CancellationToken token)
        {
            InstantiateEntered = true;
            while (PauseInstantiate) { token.ThrowIfCancellationRequested(); await Task.Yield(); }
            if (FailGuideInstantiate && (string)key == "npc/Guide/visual" && ++_guideInstances == 2)
                throw new IOException("Injected Guide instance failure");
            if (SwapVisual && (string)key == "npc/Merchant/visual") key = "npc/Guide/visual";
            return await _real.InstantiateAsync(key, parent, token);
        }
        public void Dispose() { }
    }
}
