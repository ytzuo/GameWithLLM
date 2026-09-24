using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public enum ClientContentBootstrapState
{
    NotStarted,
    LocalReady,
    AddressablesInitialize,
    CatalogCheckUpdate,
    ReleaseManifestLoad,
    CompatibilityValidation,
    RequiredDownload,
    CandidateValidation,
    Activation,
    EnableRuntimeGameUi,
    Failed
}

public sealed class ClientContentBootstrapOptions
{
    public IReadOnlyList<string> RequiredDownloadLabels { get; set; } = Array.Empty<string>();
    public IHotUpdateReleaseLoader ReleaseLoader { get; set; }
}

public sealed class ClientContentBootstrapResult
{
    private ClientContentBootstrapResult(
        bool succeeded,
        bool usedCachedCatalog,
        long downloadBytes,
        LoadedToolSetRelease loadedRelease,
        Exception candidateError,
        Exception error)
    {
        Succeeded = succeeded;
        UsedCachedCatalog = usedCachedCatalog;
        DownloadBytes = downloadBytes;
        LoadedRelease = loadedRelease;
        CandidateError = candidateError;
        Error = error;
    }

    public bool Succeeded { get; }
    public bool UsedCachedCatalog { get; }
    public long DownloadBytes { get; }
    public LoadedToolSetRelease LoadedRelease { get; }
    public Exception CandidateError { get; }
    public Exception Error { get; }

    public static ClientContentBootstrapResult Success(
        bool usedCachedCatalog,
        long downloadBytes,
        LoadedToolSetRelease loadedRelease = null,
        Exception candidateError = null) =>
        new ClientContentBootstrapResult(
            true,
            usedCachedCatalog,
            downloadBytes,
            loadedRelease,
            candidateError,
            null);

    public static ClientContentBootstrapResult Failure(Exception error) =>
        new ClientContentBootstrapResult(false, false, 0, null, null, error);
}

public interface IContentBootstrapStore
{
    bool HasLastSuccessfulContent { get; }
    void MarkSuccessfulContent();
}

public sealed class PlayerPrefsContentBootstrapStore : IContentBootstrapStore
{
    private const string LastSuccessKey = "gamewithllm.content.last-success.v1";

    public bool HasLastSuccessfulContent => PlayerPrefs.GetInt(LastSuccessKey, 0) == 1;

    public void MarkSuccessfulContent()
    {
        PlayerPrefs.SetInt(LastSuccessKey, 1);
        PlayerPrefs.Save();
    }
}

// A1 只建立可靠的 Catalog/下载门控。远端 release manifest 与候选内容从 A2 开始
// 接入，因此 A1 的 release candidate 是内置空候选，但仍完整经过状态机。
public sealed class ClientContentBootstrap
{
    private readonly IContentAssetProvider _provider;
    private readonly IContentBootstrapStore _store;
    private readonly ClientContentBootstrapOptions _options;

    public ClientContentBootstrap(
        IContentAssetProvider provider,
        IContentBootstrapStore store,
        ClientContentBootstrapOptions options = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new ClientContentBootstrapOptions();
    }

    public ClientContentBootstrapState State { get; private set; } =
        ClientContentBootstrapState.NotStarted;

    public event Action<ClientContentBootstrapState, string> StateChanged;
    public event Action<ContentDownloadProgress> DownloadProgressChanged;

    public async Task<ClientContentBootstrapResult> RunAttemptAsync(
        CancellationToken cancellationToken)
    {
        bool usedCachedCatalog = false;
        long downloadBytes = 0;
        HotUpdateReleaseManifest manifest = null;
        LoadedToolSetRelease loadedRelease = null;
        Exception candidateError = null;
        try
        {
            SetState(ClientContentBootstrapState.LocalReady, "本地启动界面已就绪");

            SetState(ClientContentBootstrapState.AddressablesInitialize, "正在初始化内容系统");
            await _provider.InitializeAsync(cancellationToken);

            SetState(ClientContentBootstrapState.CatalogCheckUpdate, "正在检查内容目录更新");
            try
            {
                IReadOnlyList<string> updates =
                    await _provider.CheckForCatalogUpdatesAsync(cancellationToken);
                if (updates.Count > 0)
                    await _provider.UpdateCatalogsAsync(updates, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception catalogError)
            {
                if (!_store.HasLastSuccessfulContent)
                    throw new InvalidOperationException(
                        "无法检查远端内容，且本机没有最后成功的内容缓存。", catalogError);
                usedCachedCatalog = true;
                Debug.LogWarning(
                    $"[Content] Remote catalog check failed; using last successful cached content: " +
                    catalogError.Message);
            }

            SetState(ClientContentBootstrapState.ReleaseManifestLoad, "正在读取发布清单");
            if (_options.ReleaseLoader != null)
            {
                try
                {
                    manifest = await _options.ReleaseLoader.LoadManifestAsync(cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { candidateError = ex; }
            }
            else
                await Task.Yield();

            SetState(ClientContentBootstrapState.CompatibilityValidation, "正在验证客户端兼容性");
            ValidatePlayerCompatibility();
            if (manifest != null && candidateError == null)
            {
                try { _options.ReleaseLoader.ValidateCompatibility(manifest); }
                catch (Exception ex) { candidateError = ex; }
            }

            SetState(ClientContentBootstrapState.RequiredDownload, "正在计算所需下载");
            downloadBytes = await _provider.GetDownloadSizeAsync(
                _options.RequiredDownloadLabels,
                cancellationToken);
            if (downloadBytes > 0)
            {
                var progress = new Progress<ContentDownloadProgress>(
                    value => DownloadProgressChanged?.Invoke(value));
                await _provider.DownloadDependenciesAsync(
                    _options.RequiredDownloadLabels,
                    progress,
                    cancellationToken);
            }
            else
            {
                DownloadProgressChanged?.Invoke(new ContentDownloadProgress(0, 0, 1));
            }
            if (manifest != null && candidateError == null)
            {
                try
                {
                    IReadOnlyList<string> artifactAddresses =
                        _options.ReleaseLoader.GetRequiredAddresses(manifest);
                    long artifactBytes = await _provider.GetDownloadSizeAsync(
                        artifactAddresses,
                        cancellationToken);
                    downloadBytes += artifactBytes;
                    if (artifactBytes > 0)
                    {
                        var progress = new Progress<ContentDownloadProgress>(
                            value => DownloadProgressChanged?.Invoke(value));
                        await _provider.DownloadDependenciesAsync(
                            artifactAddresses,
                            progress,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { candidateError = ex; }
            }

            SetState(ClientContentBootstrapState.CandidateValidation, "正在验证候选内容");
            if (!_provider.IsInitialized)
                throw new InvalidOperationException("内容系统在候选验证前失去初始化状态。");
            if (manifest != null && candidateError == null)
            {
                try
                {
                    loadedRelease = await _options.ReleaseLoader.LoadCandidateAsync(
                        manifest,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A2 候选是可选业务内容。损坏候选不能开放半套工具，但也不能让
                    // 首次安装黑屏；随后以 Player 内置工具和稳定文本键启动。
                    candidateError = ex;
                }
            }
            if (candidateError != null)
                Debug.LogError(
                    $"[Content] A2 release '{manifest?.releaseId ?? "unknown"}' was rejected; " +
                    $"using builtin tools and default text keys: {candidateError.GetBaseException().Message}");

            SetState(ClientContentBootstrapState.Activation, "正在激活内容版本");
            if (candidateError == null)
                _store.MarkSuccessfulContent();

            SetState(ClientContentBootstrapState.EnableRuntimeGameUi, "内容就绪");
            return ClientContentBootstrapResult.Success(
                usedCachedCatalog,
                downloadBytes,
                loadedRelease,
                candidateError);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetState(ClientContentBootstrapState.Failed, "内容初始化失败");
            return ClientContentBootstrapResult.Failure(ex);
        }
    }

    private void SetState(ClientContentBootstrapState state, string message)
    {
        State = state;
        StateChanged?.Invoke(state, message);
    }

    private static void ValidatePlayerCompatibility()
    {
        if (string.IsNullOrWhiteSpace(Application.version))
            throw new InvalidOperationException("Player version is empty.");

#if !UNITY_EDITOR && !UNITY_STANDALONE_WIN
        throw new PlatformNotSupportedException(
            "The current content release supports Windows Standalone players only.");
#endif
    }
}
