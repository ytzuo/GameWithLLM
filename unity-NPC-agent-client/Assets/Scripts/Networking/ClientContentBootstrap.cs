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
}

public sealed class ClientContentBootstrapResult
{
    private ClientContentBootstrapResult(
        bool succeeded,
        bool usedCachedCatalog,
        long downloadBytes,
        Exception error)
    {
        Succeeded = succeeded;
        UsedCachedCatalog = usedCachedCatalog;
        DownloadBytes = downloadBytes;
        Error = error;
    }

    public bool Succeeded { get; }
    public bool UsedCachedCatalog { get; }
    public long DownloadBytes { get; }
    public Exception Error { get; }

    public static ClientContentBootstrapResult Success(bool usedCachedCatalog, long downloadBytes) =>
        new ClientContentBootstrapResult(true, usedCachedCatalog, downloadBytes, null);

    public static ClientContentBootstrapResult Failure(Exception error) =>
        new ClientContentBootstrapResult(false, false, 0, error);
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
            await Task.Yield(); // A2 将在这里读取不可变远端 release manifest。
            cancellationToken.ThrowIfCancellationRequested();

            SetState(ClientContentBootstrapState.CompatibilityValidation, "正在验证客户端兼容性");
            ValidatePlayerCompatibility();

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

            SetState(ClientContentBootstrapState.CandidateValidation, "正在验证候选内容");
            if (!_provider.IsInitialized)
                throw new InvalidOperationException("内容系统在候选验证前失去初始化状态。");

            SetState(ClientContentBootstrapState.Activation, "正在激活内容版本");
            _store.MarkSuccessfulContent();

            SetState(ClientContentBootstrapState.EnableRuntimeGameUi, "内容就绪");
            return ClientContentBootstrapResult.Success(usedCachedCatalog, downloadBytes);
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
