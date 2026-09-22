using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

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

        public void Dispose()
        {
            IsInitialized = false;
        }
    }
}
