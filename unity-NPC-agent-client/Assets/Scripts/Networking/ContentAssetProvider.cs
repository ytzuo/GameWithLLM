using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

public interface IContentAssetProvider : IDisposable
{
    bool IsInitialized { get; }
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(CancellationToken cancellationToken);
    Task UpdateCatalogsAsync(IReadOnlyList<string> catalogIds, CancellationToken cancellationToken);
    Task<long> GetDownloadSizeAsync(IReadOnlyList<string> keys, CancellationToken cancellationToken);
    Task DownloadDependenciesAsync(
        IReadOnlyList<string> keys,
        IProgress<ContentDownloadProgress> progress,
        CancellationToken cancellationToken);
    Task<ContentAssetLease<T>> LoadAssetAsync<T>(
        object key,
        CancellationToken cancellationToken) where T : UnityEngine.Object;
    Task<ContentInstanceLease> InstantiateAsync(
        object key,
        Transform parent,
        CancellationToken cancellationToken);
}

public interface IContentSceneProvider
{
    Task<ContentSceneLease> LoadSceneAsync(
        object key,
        LoadSceneMode loadMode,
        bool activateOnLoad,
        CancellationToken cancellationToken);
    Task UnloadSceneAsync(ContentSceneLease lease, CancellationToken cancellationToken);
}

public readonly struct ContentDownloadProgress
{
    public ContentDownloadProgress(long downloadedBytes, long totalBytes, float percent)
    {
        DownloadedBytes = downloadedBytes;
        TotalBytes = totalBytes;
        Percent = Mathf.Clamp01(percent);
    }

    public long DownloadedBytes { get; }
    public long TotalBytes { get; }
    public float Percent { get; }
}

// Addressables 的唯一运行时入口。每个业务加载都返回一个必须释放的 lease，
// provider 退出时还会兜底释放仍存活的 lease。
public sealed class ContentAssetProvider : IContentAssetProvider, IContentSceneProvider
{
    private readonly object _leaseLock = new object();
    private readonly HashSet<IContentLease> _leases = new HashSet<IContentLease>();
    private bool _disposed;

    public bool IsInitialized { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsInitialized)
            return;

        AsyncOperationHandle<IResourceLocator> handle = Addressables.InitializeAsync(false);
        try
        {
            await AwaitAsync(handle, cancellationToken);
            IsInitialized = true;
        }
        finally
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
    }

    public async Task<IReadOnlyList<string>> CheckForCatalogUpdatesAsync(
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        AsyncOperationHandle<List<string>> handle = Addressables.CheckForCatalogUpdates(false);
        try
        {
            await AwaitAsync(handle, cancellationToken);
            return handle.Result?.ToArray() ?? Array.Empty<string>();
        }
        finally
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
    }

    public async Task UpdateCatalogsAsync(
        IReadOnlyList<string> catalogIds,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (catalogIds == null || catalogIds.Count == 0)
            return;

        // A1 明确不自动清旧 Bundle cache；A7 再引入清理策略。
        AsyncOperationHandle<List<IResourceLocator>> handle =
            Addressables.UpdateCatalogs(false, catalogIds, false);
        try
        {
            await AwaitAsync(handle, cancellationToken);
        }
        finally
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
    }

    public async Task<long> GetDownloadSizeAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (keys == null || keys.Count == 0)
            return 0;

        AsyncOperationHandle<long> handle = Addressables.GetDownloadSizeAsync(keys.Cast<object>());
        try
        {
            await AwaitAsync(handle, cancellationToken);
            return handle.Result;
        }
        finally
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
    }

    public async Task DownloadDependenciesAsync(
        IReadOnlyList<string> keys,
        IProgress<ContentDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        if (keys == null || keys.Count == 0)
        {
            progress?.Report(new ContentDownloadProgress(0, 0, 1));
            return;
        }

        AsyncOperationHandle handle = Addressables.DownloadDependenciesAsync(
            keys.Cast<object>(),
            Addressables.MergeMode.Union,
            false);
        try
        {
            while (!handle.IsDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadStatus status = handle.GetDownloadStatus();
                progress?.Report(new ContentDownloadProgress(
                    status.DownloadedBytes,
                    status.TotalBytes,
                    status.Percent));
                await Task.Yield();
            }
            EnsureSucceeded(handle);
            DownloadStatus completed = handle.GetDownloadStatus();
            progress?.Report(new ContentDownloadProgress(
                completed.DownloadedBytes,
                completed.TotalBytes,
                1));
        }
        finally
        {
            if (handle.IsValid())
                Addressables.Release(handle);
        }
    }

    public async Task<ContentAssetLease<T>> LoadAssetAsync<T>(
        object key,
        CancellationToken cancellationToken) where T : UnityEngine.Object
    {
        RequireInitialized();
        AsyncOperationHandle<T> handle = Addressables.LoadAssetAsync<T>(key);
        try
        {
            await AwaitAsync(handle, cancellationToken);
            var lease = new ContentAssetLease<T>(handle, RemoveLease);
            AddLease(lease);
            return lease;
        }
        catch
        {
            if (handle.IsValid())
                Addressables.Release(handle);
            throw;
        }
    }

    public async Task<ContentInstanceLease> InstantiateAsync(
        object key,
        Transform parent,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        AsyncOperationHandle<GameObject> handle = Addressables.InstantiateAsync(
            key,
            parent,
            false,
            true);
        try
        {
            await AwaitAsync(handle, cancellationToken);
            var lease = new ContentInstanceLease(handle, RemoveLease);
            AddLease(lease);
            return lease;
        }
        catch
        {
            if (handle.IsValid())
                Addressables.ReleaseInstance(handle);
            throw;
        }
    }

    public async Task<ContentSceneLease> LoadSceneAsync(
        object key,
        LoadSceneMode loadMode,
        bool activateOnLoad,
        CancellationToken cancellationToken)
    {
        RequireInitialized();
        AsyncOperationHandle<SceneInstance> handle = Addressables.LoadSceneAsync(
            key,
            loadMode,
            activateOnLoad);
        try
        {
            await AwaitAsync(handle, cancellationToken);
            var lease = new ContentSceneLease(handle, RemoveLease);
            AddLease(lease);
            return lease;
        }
        catch
        {
            if (handle.IsValid())
                Addressables.Release(handle);
            throw;
        }
    }

    public void Release(IContentLease lease) => lease?.Dispose();

    public void ReleaseInstance(ContentInstanceLease lease) => lease?.Dispose();

    public async Task UnloadSceneAsync(
        ContentSceneLease lease,
        CancellationToken cancellationToken)
    {
        if (lease == null || !lease.TryTakeHandle(out AsyncOperationHandle<SceneInstance> handle))
            return;
        RemoveLease(lease);
        // 禁用返回 handle 的自动释放，否则完成帧读取 Status 会命中 invalid handle。
        // 原 Scene handle 由 UnloadSceneAsync 消费；这里只显式释放卸载操作 handle。
        AsyncOperationHandle<SceneInstance> unload = Addressables.UnloadSceneAsync(handle, false);
        try
        {
            await AwaitAsync(unload, cancellationToken);
        }
        finally
        {
            if (unload.IsValid())
                Addressables.Release(unload);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IContentLease[] leases;
        lock (_leaseLock)
            leases = _leases.ToArray();
        foreach (IContentLease lease in leases)
            lease.Dispose();
        lock (_leaseLock)
            _leases.Clear();
        IsInitialized = false;
    }

    private void AddLease(IContentLease lease)
    {
        lock (_leaseLock)
            _leases.Add(lease);
    }

    private void RemoveLease(IContentLease lease)
    {
        lock (_leaseLock)
            _leases.Remove(lease);
    }

    private void RequireInitialized()
    {
        ThrowIfDisposed();
        if (!IsInitialized)
            throw new InvalidOperationException("Addressables content provider is not initialized.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ContentAssetProvider));
    }

    private static async Task AwaitAsync<T>(
        AsyncOperationHandle<T> handle,
        CancellationToken cancellationToken)
    {
        while (!handle.IsDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        EnsureSucceeded(handle);
    }

    private static async Task AwaitAsync(
        AsyncOperationHandle handle,
        CancellationToken cancellationToken)
    {
        while (!handle.IsDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        EnsureSucceeded(handle);
    }

    private static void EnsureSucceeded(AsyncOperationHandle handle)
    {
        if (handle.Status == AsyncOperationStatus.Succeeded)
            return;
        throw handle.OperationException ??
              new InvalidOperationException("Addressables operation failed without an exception.");
    }
}

public interface IContentLease : IDisposable
{
    bool IsReleased { get; }
}

public sealed class ContentAssetLease<T> : IContentLease where T : UnityEngine.Object
{
    private AsyncOperationHandle<T> _handle;
    private readonly Action<IContentLease> _onRelease;

    internal ContentAssetLease(AsyncOperationHandle<T> handle, Action<IContentLease> onRelease)
    {
        _handle = handle;
        _onRelease = onRelease;
    }

    public T Asset => !IsReleased ? _handle.Result : null;
    public bool IsReleased { get; private set; }

    public void Dispose()
    {
        if (IsReleased)
            return;
        IsReleased = true;
        _onRelease?.Invoke(this);
        if (_handle.IsValid())
            Addressables.Release(_handle);
    }
}

public sealed class ContentInstanceLease : IContentLease
{
    private AsyncOperationHandle<GameObject> _handle;
    private readonly Action<IContentLease> _onRelease;

    internal ContentInstanceLease(
        AsyncOperationHandle<GameObject> handle,
        Action<IContentLease> onRelease)
    {
        _handle = handle;
        _onRelease = onRelease;
    }

    public GameObject Instance => !IsReleased ? _handle.Result : null;
    public bool IsReleased { get; private set; }

    public void Dispose()
    {
        if (IsReleased)
            return;
        IsReleased = true;
        _onRelease?.Invoke(this);
        if (_handle.IsValid())
            Addressables.ReleaseInstance(_handle);
    }
}

public sealed class ContentSceneLease : IContentLease
{
    private AsyncOperationHandle<SceneInstance> _handle;
    private readonly Action<IContentLease> _onRelease;

    internal ContentSceneLease(
        AsyncOperationHandle<SceneInstance> handle,
        Action<IContentLease> onRelease)
    {
        _handle = handle;
        _onRelease = onRelease;
    }

    public SceneInstance Scene => !IsReleased ? _handle.Result : default;
    public bool IsReleased { get; private set; }

    internal bool TryTakeHandle(out AsyncOperationHandle<SceneInstance> handle)
    {
        handle = _handle;
        if (IsReleased)
            return false;
        IsReleased = true;
        return handle.IsValid();
    }

    public void Dispose()
    {
        if (!TryTakeHandle(out AsyncOperationHandle<SceneInstance> handle))
            return;
        _onRelease?.Invoke(this);
        Addressables.UnloadSceneAsync(handle, true);
    }
}
