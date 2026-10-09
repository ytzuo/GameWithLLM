using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

public sealed class NpcLibraryWindow : BaseWindow
{
    private enum LibraryTab { Installed, Discover }
    private enum LibraryStateKind { Loading, Empty, Error }
    private Button _installedTab, _discoverTab, _close, _retry, _action;
    private TextField _search;
    private VisualElement _state, _scroll, _grid, _detailAvatar;
    private Label _stateIcon, _searchPlaceholder, _stateTitle, _stateDetail, _detailName, _detailVersion, _detailDescription, _status;
    private ProgressBar _progress;
    private NpcLibraryServices _services;
    private RemoteNpcCatalog _catalog;
    private RemoteNpcSummary _selected;
    private LibraryTab _tab = LibraryTab.Discover;
    private CancellationTokenSource _lifetime;
    private string _transientStatus;
    private string _transientStatusNpcId;
    private bool _transientStatusIsError;
    private bool _searchFocused;
    private int _lifecycleGeneration;
    private int _detailGeneration;
    private readonly Dictionary<string, Texture2D> _avatars = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<Texture2D>> _avatarLoads =
        new Dictionary<string, Task<Texture2D>>(StringComparer.Ordinal);
    public event Action Closed;

    protected override void OnBindElements()
    {
        _installedTab = RootElement.Q<Button>("npc-library-installed-tab");
        _discoverTab = RootElement.Q<Button>("npc-library-discover-tab");
        _close = RootElement.Q<Button>("npc-library-close");
        _retry = RootElement.Q<Button>("npc-library-retry");
        _action = RootElement.Q<Button>("npc-library-action");
        _search = RootElement.Q<TextField>("npc-library-search");
        _state = RootElement.Q("npc-library-state");
        _scroll = RootElement.Q("npc-library-scroll");
        _grid = RootElement.Q("npc-library-grid");
        _detailAvatar = RootElement.Q("npc-library-detail-avatar");
        _stateIcon = RootElement.Q<Label>("npc-library-state-icon");
        _searchPlaceholder = RootElement.Q<Label>("npc-library-search-placeholder");
        _stateTitle = RootElement.Q<Label>("npc-library-state-title");
        _stateDetail = RootElement.Q<Label>("npc-library-state-detail");
        _detailName = RootElement.Q<Label>("npc-library-detail-name");
        _detailVersion = RootElement.Q<Label>("npc-library-detail-version");
        _detailDescription = RootElement.Q<Label>("npc-library-detail-description");
        _status = RootElement.Q<Label>("npc-library-status");
        _progress = RootElement.Q<ProgressBar>("npc-library-progress");
        _search.tooltip = "搜索 NPC 名称或描述";
        _search.label = string.Empty;

        _installedTab.clicked += () => SetTab(LibraryTab.Installed);
        _discoverTab.clicked += () => SetTab(LibraryTab.Discover);
        _close.clicked += Close;
        _retry.clicked += () => _ = ReloadAsync();
        _action.clicked += () => _ = RunActionAsync();
        _search.RegisterValueChangedCallback(_ => { UpdateSearchPlaceholder(); RenderGrid(); });
        _search.RegisterCallback<FocusInEvent>(_ => { _searchFocused = true; UpdateSearchPlaceholder(); });
        _search.RegisterCallback<FocusOutEvent>(_ => { _searchFocused = false; UpdateSearchPlaceholder(); });
    }

    protected override void OnOpen()
    {
        _services = NpcLibraryServices.Current;
        _lifecycleGeneration++;
        _lifetime = new CancellationTokenSource();
        SyncTabClasses();
        UpdateSearchPlaceholder();
        ShowEmptyDetail();
        if (_services == null)
        {
            ShowState("NPC 内容服务尚未就绪", "请关闭窗口，稍后再试。", false, LibraryStateKind.Error);
            return;
        }
        _services.Installer.Changed += OnInstallerChanged;
        _services.Installer.ProgressChanged += OnProgressChanged;
        _ = ReloadAsync();
    }

    protected override void OnClose()
    {
        _lifecycleGeneration++;
        _detailGeneration++;
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        if (_services != null)
        {
            _services.Installer.Changed -= OnInstallerChanged;
            _services.Installer.ProgressChanged -= OnProgressChanged;
            _services.CatalogClient.ReleaseAvatars();
        }
        _avatars.Clear();
        _avatarLoads.Clear();
        Closed?.Invoke();
    }

    public override void OnDestroy()
    {
        if (IsOpen) Close();
        Closed = null;
    }

    private async Task ReloadAsync()
    {
        if (_services == null || _lifetime == null) return;
        _selected = null;
        ShowEmptyDetail();
        ShowState("正在读取 NPC 列表", "只会加载列表与头像，不会下载角色模型。", false, LibraryStateKind.Loading);
        try
        {
            _catalog = await _services.CatalogClient.FetchAsync(_lifetime.Token);
            ApplyTexts();
            HideState();
            RenderGrid();
        }
        catch (OperationCanceledException) when (_lifetime == null || _lifetime.IsCancellationRequested) { }
        catch
        {
            if (_services.Installer.Installed.Count > 0)
            {
                SetTab(LibraryTab.Installed);
                _transientStatus = "无法刷新发现列表，当前显示已下载内容。";
                _transientStatusNpcId = _selected?.NpcId;
                _transientStatusIsError = true;
                SetStatus(_transientStatus, true);
            }
            else ShowState("无法读取 NPC 列表", "请检查网络连接后重试。", true, LibraryStateKind.Error);
        }
    }

    private void ApplyTexts()
    {
        _installedTab.text = Text("localTab", "已下载");
        _discoverTab.text = Text("remoteTab", "发现");
        _retry.text = Text("retry", "重试");
        _close.tooltip = Text("close", "关闭");
    }

    private void SetTab(LibraryTab tab)
    {
        ClearTransientStatus();
        _tab = tab;
        SyncTabClasses();
        RenderGrid();
    }

    private void SyncTabClasses()
    {
        _installedTab.EnableInClassList("npc-library-tab--active", _tab == LibraryTab.Installed);
        _discoverTab.EnableInClassList("npc-library-tab--active", _tab == LibraryTab.Discover);
    }

    private IEnumerable<RemoteNpcSummary> VisibleEntries()
    {
        IEnumerable<RemoteNpcSummary> items;
        if (_tab == LibraryTab.Discover)
            items = _catalog?.Npcs ?? Array.Empty<RemoteNpcSummary>();
        else
        {
            var remote = (_catalog?.Npcs ?? Array.Empty<RemoteNpcSummary>()).ToDictionary(x => x.NpcId, StringComparer.Ordinal);
            items = _services.Installer.Installed.Select(record =>
                remote.TryGetValue(record.NpcId, out RemoteNpcSummary summary) &&
                string.Equals(summary.ContentVersion, record.ContentVersion, StringComparison.Ordinal) &&
                string.Equals(summary.ManifestSha256, record.ManifestSha256, StringComparison.Ordinal)
                    ? summary
                    : RemoteNpcSummary.FromInstalled(record));
        }
        string query = (_search.value ?? string.Empty).Trim();
        if (query.Length > 0) items = items.Where(x => x.DisplayName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 || x.Description.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        return items;
    }

    private void RenderGrid()
    {
        if (_services == null) return;
        _grid.Clear();
        List<RemoteNpcSummary> entries = VisibleEntries().ToList();
        if (entries.Count == 0)
        {
            _selected = null;
            ShowEmptyDetail();
            ShowState(_tab == LibraryTab.Installed ? Text("localEmpty", "暂无已下载 NPC") : Text("remoteEmpty", "暂无可发现 NPC"),
                string.IsNullOrWhiteSpace(_search.value) ? "这里会显示符合当前分类的角色。" : "没有匹配搜索条件的角色。", false, LibraryStateKind.Empty);
            return;
        }
        HideState();
        if (_selected == null || entries.All(x => x.NpcId != _selected.NpcId))
        {
            if (_selected != null &&
                !string.Equals(_selected.NpcId, entries[0].NpcId, StringComparison.Ordinal))
                ClearTransientStatus();
            _selected = entries[0];
        }
        foreach (RemoteNpcSummary summary in entries)
        {
            var card = new Button { name = $"npc-card-{summary.NpcId}" };
            card.tooltip = summary.DisplayName;
            card.AddToClassList("npc-library-card");
            card.EnableInClassList("npc-library-card--selected", summary.NpcId == _selected.NpcId);
            AddPlaceholder(card, "npc-library-card-placeholder");
            var strip = new VisualElement(); strip.AddToClassList("npc-library-card-name-strip");
            var name = new Label(summary.DisplayName); name.AddToClassList("npc-library-card-name"); strip.Add(name); card.Add(strip);
            if (_tab == LibraryTab.Discover && _services.Installer.TryGet(summary.NpcId, out _))
            { var badge = new Label("✓ " + Text("installed", "已下载")); badge.AddToClassList("npc-library-installed-badge"); card.Add(badge); }
            card.clicked += () =>
            {
                ClearTransientStatus();
                _selected = summary;
                string focusName = card.name;
                RenderGrid();
                RootElement.schedule.Execute(() => RootElement.Q<Button>(focusName)?.Focus());
            };
            _grid.Add(card);
            _ = LoadAvatarAsync(summary, card, false, _lifecycleGeneration, -1);
        }
        _ = RenderDetailAsync();
    }

    private async Task LoadAvatarAsync(
        RemoteNpcSummary summary,
        VisualElement target,
        bool detail,
        int lifecycleGeneration,
        int detailGeneration)
    {
        try
        {
            if (!_avatars.TryGetValue(summary.NpcId, out Texture2D texture))
            {
                if (!_avatarLoads.TryGetValue(summary.NpcId, out Task<Texture2D> load))
                {
                    load = _services.CatalogClient.LoadAvatarAsync(summary, _lifetime.Token);
                    _avatarLoads.Add(summary.NpcId, load);
                }
                try
                {
                    texture = await load;
                    _avatars[summary.NpcId] = texture;
                }
                finally
                {
                    if (_avatarLoads.TryGetValue(summary.NpcId, out Task<Texture2D> current) &&
                        ReferenceEquals(current, load))
                        _avatarLoads.Remove(summary.NpcId);
                }
            }
            if (_lifetime == null || target == null || target.panel == null ||
                lifecycleGeneration != _lifecycleGeneration)
                return;
            if (detail && (detailGeneration != _detailGeneration ||
                           !string.Equals(_selected?.NpcId, summary.NpcId, StringComparison.Ordinal)))
                return;
            target.style.backgroundImage = new StyleBackground(texture);
            VisualElement placeholder = target.Q(className: "npc-library-card-placeholder") ??
                                        target.Q(className: "npc-library-placeholder");
            if (placeholder != null) placeholder.style.display = DisplayStyle.None;
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (detail && _lifetime != null && lifecycleGeneration == _lifecycleGeneration &&
                detailGeneration == _detailGeneration &&
                string.Equals(_selected?.NpcId, summary.NpcId, StringComparison.Ordinal))
                SetStatus("头像暂时无法显示。", false);
        }
    }

    private async Task RenderDetailAsync()
    {
        RemoteNpcSummary summary = _selected;
        int lifecycleGeneration = _lifecycleGeneration;
        int detailGeneration = ++_detailGeneration;
        if (summary == null)
        {
            ShowEmptyDetail();
            return;
        }
        _detailName.text = summary.DisplayName;
        _detailName.tooltip = summary.DisplayName;
        _detailVersion.text = $"版本 {summary.ContentVersion}";
        _detailDescription.text = summary.Description;
        _detailAvatar.style.backgroundImage = new StyleBackground(StyleKeyword.Null);
        foreach (VisualElement child in _detailAvatar.Children()) child.style.display = DisplayStyle.Flex;
        _ = LoadAvatarAsync(summary, _detailAvatar, true, lifecycleGeneration, detailGeneration);
        _progress.AddToClassList("npc-library-progress--hidden");
        _status.RemoveFromClassList("npc-library-status--error");

        bool installed = _services.Installer.TryGet(summary.NpcId, out InstalledNpcRecord record);
        bool selectedVersionInstalled = installed &&
            string.Equals(record.ContentVersion, summary.ContentVersion, StringComparison.Ordinal) &&
            string.Equals(record.ManifestSha256, summary.ManifestSha256, StringComparison.Ordinal);
        if (_services.Spawner.IsSpawned(summary.NpcId)) { SetAction(Text("alreadySpawned", "已在场景中"), false); SetStatus(Text("alreadySpawned", "已在场景中"), false); return; }
        if (_services.Installer.ActiveNpcId == summary.NpcId) { SetAction(Text("cancel", "取消"), true); SetStatus(Text("downloading", "下载中"), false); _progress.RemoveFromClassList("npc-library-progress--hidden"); return; }
        if (_services.Installer.ActiveNpcId != null) { SetAction(Text("download", "下载"), false); SetStatus(Text("errorBusy", "已有下载任务，请稍后重试"), false); return; }
        if (selectedVersionInstalled)
        {
            try
            {
                CancellationToken lifetimeToken = _lifetime.Token;
                bool missing = await _services.Installer.IsCacheMissingAsync(record.NpcId, lifetimeToken);
                if (_lifetime == null || lifecycleGeneration != _lifecycleGeneration ||
                    detailGeneration != _detailGeneration || _selected?.NpcId != summary.NpcId)
                    return;
                SetAction(missing ? Text("retry", "修复") : Text("spawn", "生成"), true);
                SetStatus(missing ? "本地缓存不完整，需要重新下载。" : Text("installed", "已下载"), missing);
            }
            catch (OperationCanceledException) { }
            catch
            {
                if (_lifetime != null && lifecycleGeneration == _lifecycleGeneration &&
                    detailGeneration == _detailGeneration && _selected?.NpcId == summary.NpcId)
                {
                    SetAction(Text("retry", "修复"), true);
                    SetStatus("无法确认本地缓存状态。", true);
                }
            }
            return;
        }
        bool catalogMismatch = !string.IsNullOrEmpty(_services.ActiveCatalogContentVersion) && _catalog != null &&
            !string.Equals(_services.ActiveCatalogContentVersion, _catalog.CatalogContentVersion, StringComparison.Ordinal);
        SetAction(Text("download", installed ? "更新并下载" : "下载"), !catalogMismatch && IsCompatible(summary));
        SetStatus(catalogMismatch ? Text("restartRequired", "重启游戏后可下载此版本") :
            IsCompatible(summary) ? "尚未下载" : Text("errorCompatibility", "NPC 与当前 Player 不兼容"), catalogMismatch || !IsCompatible(summary));
    }

    private async Task RunActionAsync()
    {
        RemoteNpcSummary summary = _selected;
        if (summary == null || _services == null) return;
        if (_services.Installer.ActiveNpcId == summary.NpcId) { _services.Installer.Cancel(); return; }
        ClearTransientStatus();
        _action.SetEnabled(false);
        bool spawnPhase = false;
        try
        {
            if (_services.Installer.TryGet(summary.NpcId, out InstalledNpcRecord record) &&
                string.Equals(record.ContentVersion, summary.ContentVersion, StringComparison.Ordinal) &&
                string.Equals(record.ManifestSha256, summary.ManifestSha256, StringComparison.Ordinal) &&
                !await _services.Installer.IsCacheMissingAsync(summary.NpcId, _lifetime.Token))
            {
                spawnPhase = true;
                await _services.Spawner.SpawnAsync(record, _lifetime.Token);
                SetStatus(Text("alreadySpawned", "已在场景中"), false);
            }
            else
            {
                record = await _services.Installer.InstallAsync(summary, _lifetime.Token);
                spawnPhase = true;
                await _services.Spawner.SpawnAsync(record, _lifetime.Token);
                SetStatus(Text("alreadySpawned", "已在场景中"), false);
            }
        }
        catch (OperationCanceledException)
        {
            _transientStatus = Text("errorCancelled", "下载已取消");
            _transientStatusNpcId = summary.NpcId;
            _transientStatusIsError = false;
        }
        catch (Exception)
        {
            _transientStatus = spawnPhase
                ? Text("errorSpawn", "生成失败，请重试")
                : Text("errorDownload", "下载失败，请重试");
            _transientStatusNpcId = summary.NpcId;
            _transientStatusIsError = true;
        }
        finally { await RenderDetailAsync(); RenderGrid(); }
    }

    private void OnInstallerChanged() { if (_lifetime != null) RenderGrid(); }
    private void OnProgressChanged(string npcId, ContentDownloadProgress progress)
    {
        if (_selected?.NpcId != npcId || _lifetime == null) return;
        _progress.value = progress.Percent;
        _progress.RemoveFromClassList("npc-library-progress--hidden");
        SetStatus(Text("progressFormat", "已下载 {downloaded} / {total} 字节").Replace("{downloaded}", FormatBytes(progress.DownloadedBytes)).Replace("{total}", FormatBytes(progress.TotalBytes)), false);
    }

    private void ShowState(string title, string detail, bool retry, LibraryStateKind kind)
    {
        _stateTitle.text = title;
        _stateDetail.text = detail;
        _retry.style.display = retry ? DisplayStyle.Flex : DisplayStyle.None;
        _stateIcon.text = kind == LibraryStateKind.Loading ? "···" : kind == LibraryStateKind.Error ? "!" : "—";
        _stateIcon.EnableInClassList("npc-library-state-icon--loading", kind == LibraryStateKind.Loading);
        _stateIcon.EnableInClassList("npc-library-state-icon--empty", kind == LibraryStateKind.Empty);
        _stateIcon.EnableInClassList("npc-library-state-icon--error", kind == LibraryStateKind.Error);
        _state.RemoveFromClassList("npc-library-state--hidden");
        _scroll.style.display = DisplayStyle.None;
    }
    private void HideState() { _state.AddToClassList("npc-library-state--hidden"); _scroll.style.display = DisplayStyle.Flex; }
    private void SetAction(string text, bool enabled) { _action.text = text; _action.SetEnabled(enabled); }
    private void SetStatus(string text, bool error)
    {
        bool useTransient = _transientStatus != null &&
                            string.Equals(
                                _transientStatusNpcId,
                                _selected?.NpcId,
                                StringComparison.Ordinal);
        _status.text = useTransient ? _transientStatus : text;
        _status.EnableInClassList(
            "npc-library-status--error",
            useTransient ? _transientStatusIsError : error);
    }
    private void ClearTransientStatus()
    {
        _transientStatus = null;
        _transientStatusNpcId = null;
        _transientStatusIsError = false;
    }
    private void ShowEmptyDetail()
    {
        _detailGeneration++;
        _detailAvatar.style.backgroundImage = new StyleBackground(StyleKeyword.Null);
        foreach (VisualElement child in _detailAvatar.Children())
            child.style.display = DisplayStyle.Flex;
        _detailName.text = "选择一个 NPC";
        _detailVersion.text = string.Empty;
        _detailDescription.text = "从左侧列表选择角色，查看内容说明与可用操作。";
        _progress.AddToClassList("npc-library-progress--hidden");
        SetStatus(string.Empty, false);
        SetAction(Text("download", "下载"), false);
    }
    private void UpdateSearchPlaceholder()
    {
        bool hidden = _searchFocused || !string.IsNullOrEmpty(_search.value);
        _searchPlaceholder.EnableInClassList("npc-library-search-placeholder--hidden", hidden);
    }
    private string Text(string key, string fallback) => _catalog != null && _catalog.Texts.TryGetValue(key, out string value) ? value : fallback;
    private static string FormatBytes(long value) => value >= 1048576 ? $"{value / 1048576f:0.0} MB" : value >= 1024 ? $"{value / 1024f:0.0} KB" : $"{value} B";
    private static bool IsCompatible(RemoteNpcSummary summary)
        => NpcContentInstaller.IsCompatiblePlayerVersion(summary, Application.version);
    private static void AddPlaceholder(VisualElement parent, string className)
    { var wrapper = new VisualElement(); wrapper.AddToClassList(className); var icon = new VisualElement(); icon.AddToClassList("npc-library-placeholder"); var head = new VisualElement(); head.AddToClassList("npc-library-placeholder-head"); var body = new VisualElement(); body.AddToClassList("npc-library-placeholder-body"); icon.Add(head); icon.Add(body); wrapper.Add(icon); parent.Add(wrapper); }
}
