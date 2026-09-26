using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class ClientContentBootstrapTests
{
    [Test]
    public async Task SuccessfulAttemptRunsEveryStageInOrderAndMarksCache()
    {
        var provider = new FakeProvider
        {
            CatalogUpdates = new[] { "catalog-a" },
            DownloadSize = 128
        };
        var store = new FakeStore();
        var bootstrap = new ClientContentBootstrap(
            provider,
            store,
            new ClientContentBootstrapOptions
            {
                RequiredDownloadLabels = new[] { "content.bootstrap" }
            });
        var states = new List<ClientContentBootstrapState>();
        bootstrap.StateChanged += (state, _) => states.Add(state);

        ClientContentBootstrapResult result =
            await bootstrap.RunAttemptAsync(CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.UsedCachedCatalog, Is.False);
        Assert.That(result.DownloadBytes, Is.EqualTo(128));
        Assert.That(store.MarkCount, Is.EqualTo(1));
        Assert.That(provider.UpdateCount, Is.EqualTo(1));
        Assert.That(provider.DownloadCount, Is.EqualTo(1));
        Assert.That(states, Is.EqualTo(new[]
        {
            ClientContentBootstrapState.LocalReady,
            ClientContentBootstrapState.AddressablesInitialize,
            ClientContentBootstrapState.CatalogCheckUpdate,
            ClientContentBootstrapState.ReleaseManifestLoad,
            ClientContentBootstrapState.CompatibilityValidation,
            ClientContentBootstrapState.RequiredDownload,
            ClientContentBootstrapState.CandidateValidation,
            ClientContentBootstrapState.Activation,
            ClientContentBootstrapState.EnableRuntimeGameUi
        }));
    }

    [Test]
    public async Task CatalogFailureUsesLastSuccessfulCache()
    {
        var provider = new FakeProvider { CatalogError = new Exception("offline") };
        var store = new FakeStore { HasLastSuccessfulContent = true };
        var bootstrap = new ClientContentBootstrap(provider, store);

        ClientContentBootstrapResult result =
            await bootstrap.RunAttemptAsync(CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.UsedCachedCatalog, Is.True);
        Assert.That(bootstrap.State, Is.EqualTo(ClientContentBootstrapState.EnableRuntimeGameUi));
    }

    [Test]
    public async Task FirstRunCatalogFailureKeepsRuntimeBlocked()
    {
        var provider = new FakeProvider { CatalogError = new Exception("offline") };
        var bootstrap = new ClientContentBootstrap(provider, new FakeStore());

        ClientContentBootstrapResult result =
            await bootstrap.RunAttemptAsync(CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Error, Is.Not.Null);
        Assert.That(bootstrap.State, Is.EqualTo(ClientContentBootstrapState.Failed));
    }

    [Test]
    public void CancellationIsNotConvertedIntoRetryableFailure()
    {
        var provider = new FakeProvider();
        var bootstrap = new ClientContentBootstrap(provider, new FakeStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await bootstrap.RunAttemptAsync(cancellation.Token));
    }

    [Test]
    public async Task InvalidA2CandidateFallsBackToBuiltinWithoutMarkingReleaseSuccessful()
    {
        var provider = new FakeProvider();
        var store = new FakeStore();
        var bootstrap = new ClientContentBootstrap(
            provider,
            store,
            new ClientContentBootstrapOptions
            {
                ReleaseLoader = new FailingReleaseLoader()
            });
        LogAssert.Expect(
            LogType.Error,
            "[Content] Release 'unknown' was rejected; using builtin tools and default text keys " +
            "(code=HOT_UPDATE_CANDIDATE_REJECTED): corrupt manifest");

        ClientContentBootstrapResult result =
            await bootstrap.RunAttemptAsync(CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.LoadedRelease, Is.Null);
        Assert.That(result.CandidateError, Is.TypeOf<System.IO.InvalidDataException>());
        Assert.That(result.CandidateErrorCode, Is.EqualTo("HOT_UPDATE_CANDIDATE_REJECTED"));
        Assert.That(store.MarkCount, Is.Zero);
        Assert.That(bootstrap.State, Is.EqualTo(ClientContentBootstrapState.EnableRuntimeGameUi));
    }

    [Test]
    public async Task ValidA2Candidate_IsMarkedSuccessfulOnlyAfterRegistryActivationConfirmation()
    {
        var provider = new FakeProvider();
        var store = new FakeStore();
        var bootstrap = new ClientContentBootstrap(
            provider,
            store,
            new ClientContentBootstrapOptions
            {
                ReleaseLoader = new SuccessfulReleaseLoader()
            });

        ClientContentBootstrapResult result =
            await bootstrap.RunAttemptAsync(CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.LoadedRelease, Is.Not.Null);
        Assert.That(store.MarkCount, Is.Zero);
        bootstrap.ConfirmActivation();
        bootstrap.ConfirmActivation();
        Assert.That(store.MarkCount, Is.EqualTo(1));
    }

    private sealed class FakeStore : IContentBootstrapStore
    {
        public bool HasLastSuccessfulContent { get; set; }
        public int MarkCount { get; private set; }

        public void MarkSuccessfulContent()
        {
            HasLastSuccessfulContent = true;
            MarkCount++;
        }
    }

    private sealed class FakeProvider : IContentAssetProvider
    {
        public bool IsInitialized { get; private set; }
        public IReadOnlyList<string> CatalogUpdates { get; set; } = Array.Empty<string>();
        public Exception CatalogError { get; set; }
        public long DownloadSize { get; set; }
        public int UpdateCount { get; private set; }
        public int DownloadCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsInitialized = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CatalogError != null)
                throw CatalogError;
            return Task.FromResult(CatalogUpdates);
        }

        public Task UpdateCatalogsAsync(
            IReadOnlyList<string> catalogIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateCount++;
            return Task.CompletedTask;
        }

        public Task<long> GetDownloadSizeAsync(
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DownloadSize);
        }

        public Task DownloadDependenciesAsync(
            IReadOnlyList<string> keys,
            IProgress<ContentDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCount++;
            progress?.Report(new ContentDownloadProgress(DownloadSize, DownloadSize, 1));
            return Task.CompletedTask;
        }

        public Task<ContentAssetLease<T>> LoadAssetAsync<T>(
            object key,
            CancellationToken cancellationToken) where T : UnityEngine.Object =>
            throw new NotSupportedException();

        public Task<ContentInstanceLease> InstantiateAsync(
            object key,
            Transform parent,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Dispose()
        {
            IsInitialized = false;
        }
    }

    private sealed class FailingReleaseLoader : IHotUpdateReleaseLoader
    {
        public Task<HotUpdateReleaseManifest> LoadManifestAsync(CancellationToken cancellationToken) =>
            Task.FromException<HotUpdateReleaseManifest>(
                new System.IO.InvalidDataException("corrupt manifest"));

        public void ValidateCompatibility(HotUpdateReleaseManifest manifest) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> GetRequiredAddresses(HotUpdateReleaseManifest manifest) =>
            throw new NotSupportedException();

        public Task<LoadedToolSetRelease> LoadCandidateAsync(
            HotUpdateReleaseManifest manifest,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class SuccessfulReleaseLoader : IHotUpdateReleaseLoader
    {
        private readonly HotUpdateReleaseManifest _manifest = new HotUpdateReleaseManifest
        {
            releaseId = "test-release"
        };

        public Task<HotUpdateReleaseManifest> LoadManifestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_manifest);

        public void ValidateCompatibility(HotUpdateReleaseManifest manifest)
        {
        }

        public IReadOnlyList<string> GetRequiredAddresses(HotUpdateReleaseManifest manifest) =>
            Array.Empty<string>();

        public Task<LoadedToolSetRelease> LoadCandidateAsync(
            HotUpdateReleaseManifest manifest,
            CancellationToken cancellationToken) => Task.FromResult(new LoadedToolSetRelease(
                manifest.releaseId,
                "1.0.0",
                "1.0.0",
                Array.Empty<ToolReleaseDeclaration>(),
                Array.Empty<RetiredToolDeclaration>(),
                Array.Empty<ToolHistoryDeclaration>(),
                Array.Empty<LoadedToolPack>(),
                "{}",
                "{}",
                "{}"));
    }
}
