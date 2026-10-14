using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AddressableAssets;

public sealed class NpcPresentationStage3Tests
{
    private static RemoteNpcManifest Manifest() => RemoteNpcContract.ParseManifest(File.ReadAllText(
        Path.Combine(Application.dataPath, "../../NpcContent/npc/Merchant/npc.json")));

    [Test]
    public void MissingScriptIsRejectedExplicitly()
    {
        string path = "Assets/Tests/Editor/stage3-missing-script.prefab";
        var root = new GameObject("missing script fixture");
        root.SetActive(false);
        try
        {
            var behaviour = root.AddComponent<CharacterVisualController>();
            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromMonoBehaviour(behaviour)));
            PrefabUtility.SaveAsPrefabAsset(root, path);
            File.WriteAllText(path, File.ReadAllText(path).Replace(guid, "11111111111111111111111111111111"));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var error = Assert.Throws<InvalidOperationException>(() => CharacterVisualController.ValidateVisualInstance(
                AssetDatabase.LoadAssetAtPath<GameObject>(path), true));
            StringAssert.Contains("Missing Script", error.Message);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); AssetDatabase.DeleteAsset(path); }
    }

    [Test]
    public async Task CompleteOfflineCacheExercisesInstanceAndReleasesAllLeases()
    {
        using var provider = new FaultProvider();
        await NpcPresentationPreparation.ValidateAsync(provider, Manifest(), CancellationToken.None);
        Assert.AreEqual(1, provider.Instantiated);
        Assert.AreEqual(0, provider.LiveLeases);
        Assert.AreEqual(0, provider.Instances.Count);
    }

    [Test]
    public void ZeroDownloadSizeDoesNotHideMissingAddressOrDependency()
    {
        using var provider = new FaultProvider { AddressExists = false };
        var missing = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await NpcPresentationPreparation.ValidateAsync(provider, Manifest(), CancellationToken.None));
        Assert.AreEqual("NPC_RESOURCE_RESTART_REQUIRED", missing.Message);
        provider.AddressExists = true;
        provider.LoadFailure = true;
        Assert.ThrowsAsync<IOException>(async () =>
            await NpcPresentationPreparation.ValidateAsync(provider, Manifest(), CancellationToken.None));
        Assert.AreEqual(0, provider.LiveLeases);
    }

    [Test]
    public void CorruptPrefabAndAnimatorParametersCannotPassPreparation()
    {
        using var provider = new FaultProvider();
        provider.Prefab = new GameObject("broken visual");
        try
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await NpcPresentationPreparation.ValidateAsync(provider, Manifest(), CancellationToken.None));
            Assert.AreEqual(0, provider.LiveLeases);
        }
        finally { UnityEngine.Object.DestroyImmediate(provider.Prefab); }

        string path = "Assets/Tests/Editor/stage3-invalid-controller.controller";
        provider.Prefab = UnityEngine.Object.Instantiate(FaultProvider.ValidPrefab);
        try
        {
            provider.Prefab.GetComponentInChildren<Animator>(true).runtimeAnimatorController =
                AnimatorController.CreateAnimatorControllerAtPath(path);
            var error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await NpcPresentationPreparation.ValidateAsync(provider, Manifest(), CancellationToken.None));
            StringAssert.Contains("Speed(float)", error.Message);
            Assert.AreEqual(0, provider.LiveLeases);
            Assert.IsEmpty(provider.Instances);
        }
        finally { UnityEngine.Object.DestroyImmediate(provider.Prefab); AssetDatabase.DeleteAsset(path); }
    }

    [Test]
    public void InvalidDllBytesAndEntryContractReleaseVisualAndAssembly()
    {
        using var provider = new FaultProvider();
        var raw = JObject.Parse(Manifest().RawJson);
        var dll = new TextAsset("invalid DLL bytes");
        provider.Dll = dll;
        raw["animationDriver"] = new JObject
        {
            ["kind"] = "hotUpdate", ["address"] = "npc/Merchant/animation-script",
            ["assemblyName"] = "Stage3InvalidAssembly", ["entryType"] = "Invalid.Entry",
            ["length"] = dll.bytes.Length, ["sha256"] = RemoteNpcContract.ComputeSha256(dll.bytes)
        };
        try
        {
            Assert.ThrowsAsync<BadImageFormatException>(async () =>
                await NpcPresentationPreparation.ValidateAsync(provider,
                    RemoteNpcContract.ParseManifest(raw.ToString()), CancellationToken.None));
            Assert.AreEqual(0, provider.LiveLeases);
            Assert.IsEmpty(provider.Instances);
            raw["animationDriver"]["assemblyName"] = typeof(ContentAssetProvider).Assembly.GetName().Name;
            raw["animationDriver"]["entryType"] = typeof(ContentAssetProvider).FullName;
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NpcPresentationPreparation.ValidateAsync(provider,
                    RemoteNpcContract.ParseManifest(raw.ToString()), CancellationToken.None));
            raw["animationDriver"]["sha256"] = new string('0', 64);
            Assert.ThrowsAsync<InvalidDataException>(async () =>
                await NpcPresentationPreparation.ValidateAsync(provider,
                    RemoteNpcContract.ParseManifest(raw.ToString()), CancellationToken.None));
            Assert.AreEqual(0, provider.LiveLeases);
        }
        finally { UnityEngine.Object.DestroyImmediate(dll); }
    }

    [Test]
    public void CancellationAfterCompletedLoadReleasesCandidate()
    {
        using var provider = new FaultProvider();
        using var cancellation = new CancellationTokenSource();
        provider.AfterInstantiate = cancellation.Cancel;
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await NpcPresentationPreparation.ValidateAsync(provider, Manifest(), cancellation.Token));
        Assert.AreEqual(0, provider.LiveLeases);
        Assert.IsEmpty(provider.Instances);
    }

    [Test]
    public async Task InstalledRecordWorksOfflineAndDetectsEvictionWithoutChangingRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), "npc-stage3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var record = new InstalledNpcRecord { NpcId = "Merchant", DisplayName = "展示名",
                Description = "当前资源", AvatarPath = "npc/Merchant/avatar.png", ManifestJson = Manifest().RawJson };
            File.WriteAllText(Path.Combine(root, "installed-current.json"), JArray.FromObject(new[] { record }).ToString());
            using var provider = new FaultProvider();
            using var client = new RemoteNpcCatalogClient(new Uri("http://127.0.0.1:1/"), root, TimeSpan.FromSeconds(1));
            var installer = new NpcContentInstaller(provider, client, root);
            Assert.False(await installer.IsCacheMissingAsync("Merchant", CancellationToken.None));
            await installer.ValidateInstalledAsync("Merchant", CancellationToken.None);
            provider.DownloadSize = 512;
            Assert.True(await installer.IsCacheMissingAsync("Merchant", CancellationToken.None));
            Assert.True(installer.TryGet("Merchant", out var retained));
            Assert.AreEqual(record.ManifestJson, retained.ManifestJson);
            provider.DownloadSize = 0;
            await installer.ValidateInstalledAsync("Merchant", CancellationToken.None);
            Assert.AreEqual(0, provider.LiveLeases);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FaultProvider : IContentAssetProvider
    {
        public static GameObject ValidPrefab => AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Content/Npcs/Merchant/Visual.prefab");
        public GameObject Prefab = ValidPrefab;
        public TextAsset Dll;
        public bool AddressExists = true, LoadFailure;
        public long DownloadSize;
        public int LiveLeases, Instantiated;
        public Action AfterInstantiate;
        public readonly List<GameObject> Instances = new List<GameObject>();
        public bool IsInitialized => true;
        public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task UpdateCatalogsAsync(IReadOnlyList<string> ids, CancellationToken token) => Task.CompletedTask;
        public Task<bool> AddressExistsAsync(string key, Type type, CancellationToken token) => Task.FromResult(AddressExists);
        public Task<long> GetDownloadSizeAsync(IReadOnlyList<string> keys, CancellationToken token) => Task.FromResult(DownloadSize);
        public Task DownloadDependenciesAsync(IReadOnlyList<string> keys, IProgress<ContentDownloadProgress> progress, CancellationToken token) { DownloadSize = 0; return Task.CompletedTask; }
        public Task<ContentAssetLease<T>> LoadAssetAsync<T>(object key, CancellationToken token) where T : UnityEngine.Object
        {
            token.ThrowIfCancellationRequested();
            if (LoadFailure) throw new IOException("Missing dependency");
            var asset = (T)(UnityEngine.Object)(typeof(T) == typeof(GameObject) ? (UnityEngine.Object)Prefab : Dll);
            var handle = Addressables.ResourceManager.CreateCompletedOperation(asset, null);
            LiveLeases++;
            return Task.FromResult((ContentAssetLease<T>)Activator.CreateInstance(typeof(ContentAssetLease<T>),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { handle, (Action<IContentLease>)(_ => LiveLeases--) }, null));
        }
        public Task<ContentInstanceLease> InstantiateAsync(object key, Transform parent, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var instance = UnityEngine.Object.Instantiate(Prefab, parent);
            Instances.Add(instance); Instantiated++; LiveLeases++;
            var handle = Addressables.ResourceManager.CreateCompletedOperation(instance, null);
            var lease = (ContentInstanceLease)Activator.CreateInstance(typeof(ContentInstanceLease),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { handle, (Action<IContentLease>)(_ => { LiveLeases--; Instances.Remove(instance); UnityEngine.Object.DestroyImmediate(instance); }) }, null);
            AfterInstantiate?.Invoke();
            return Task.FromResult(lease);
        }
        public void Dispose() { Assert.AreEqual(0, LiveLeases); Assert.IsEmpty(Instances); }
    }
}
