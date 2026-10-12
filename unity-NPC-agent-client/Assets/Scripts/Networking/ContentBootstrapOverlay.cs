using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// 只使用 Player 内置 IMGUI，不依赖 Catalog、Bundle、字体资产或业务 UIDocument，
// 因而 Addressables 初始化失败时仍能显示错误并接受重试。
public sealed class ContentBootstrapOverlay : MonoBehaviour
{
    private readonly object _retryLock = new object();
    private TaskCompletionSource<bool> _retrySource;
    private string _message = "正在准备本地内容…";
    private string _error;
    private float _progress;
    private long _downloadedBytes;
    private long _totalBytes;
    private bool _visible = true;

    public void Bind(ClientContentBootstrap bootstrap)
    {
        if (bootstrap == null)
            throw new ArgumentNullException(nameof(bootstrap));
        bootstrap.StateChanged += OnStateChanged;
        bootstrap.DownloadProgressChanged += OnDownloadProgressChanged;
    }

    public void ShowFailure(Exception error)
    {
        _error = error?.GetBaseException().Message ?? "未知内容错误";
        _message = "内容准备失败";
        _visible = true;
        lock (_retryLock)
            _retrySource = new TaskCompletionSource<bool>();
    }

    public Task WaitForRetryAsync(CancellationToken cancellationToken)
    {
        Task retryTask;
        lock (_retryLock)
        {
            _retrySource ??= new TaskCompletionSource<bool>();
            retryTask = _retrySource.Task;
        }
        return AwaitRetryAsync(retryTask, cancellationToken);
    }

    public void Hide()
    {
        _visible = false;
        _error = null;
    }

    private void OnStateChanged(ClientContentBootstrapState state, string message)
    {
        _message = message;
        if (state != ClientContentBootstrapState.Failed)
            _error = null;
    }

    private void OnDownloadProgressChanged(ContentDownloadProgress value)
    {
        _progress = value.Percent;
        _downloadedBytes = value.DownloadedBytes;
        _totalBytes = value.TotalBytes;
    }

    private void OnGUI()
    {
        if (!_visible)
            return;

        const float width = 520;
        const float height = 230;
        Rect area = new Rect(
            Mathf.Max(16, (Screen.width - width) * 0.5f),
            Mathf.Max(16, (Screen.height - height) * 0.5f),
            Mathf.Min(width, Screen.width - 32),
            height);
        GUI.Box(area, GUIContent.none);
        GUILayout.BeginArea(new Rect(area.x + 24, area.y + 20, area.width - 48, area.height - 40));
        GUILayout.Label("GameWithLLM 内容更新");
        GUILayout.Space(12);
        GUILayout.Label(_message ?? string.Empty);

        Rect progressRect = GUILayoutUtility.GetRect(10, 22, GUILayout.ExpandWidth(true));
        GUI.Box(progressRect, GUIContent.none);
        Rect fill = progressRect;
        fill.width *= Mathf.Clamp01(_progress);
        GUI.Box(fill, GUIContent.none);
        if (_totalBytes > 0)
            GUILayout.Label($"{FormatBytes(_downloadedBytes)} / {FormatBytes(_totalBytes)}");

        if (!string.IsNullOrWhiteSpace(_error))
        {
            GUILayout.Space(8);
            GUILayout.Label(_error);
            GUILayout.Space(8);
            if (GUILayout.Button("重试", GUILayout.Height(34)))
            {
                lock (_retryLock)
                {
                    _retrySource?.TrySetResult(true);
                    _retrySource = null;
                }
                _error = null;
                _message = "正在重试…";
            }
        }
        GUILayout.EndArea();
    }

    private static async Task AwaitRetryAsync(Task retryTask, CancellationToken cancellationToken)
    {
        while (!retryTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        await retryTask;
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1024L * 1024L)
            return $"{value / (1024f * 1024f):0.0} MB";
        if (value >= 1024L)
            return $"{value / 1024f:0.0} KB";
        return $"{value} B";
    }
}
