using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.UIElements;

public static class UiContentIds
{
    public const string PanelSettings = "ui/panel/default";
    public const string GameplayHud = "ui/hud/gameplay";
    public const string ChatWindow = "ui/window/chat";
    public const string InventoryListWindow = "ui/window/inventory-list";
    public const string InventoryInteractWindow = "ui/window/inventory-interact";
    public const string InventoryWindow = "ui/window/inventory";
    public const string ItemDispenserWindow = "ui/window/item-dispenser";
    public const string SaveGameWindow = "ui/window/save-game";
    public const string SystemMessageTemplate = "ui/template/chat/system-message";
    public const string PlayerMessageTemplate = "ui/template/chat/player-message";
    public const string OpponentMessageTemplate = "ui/template/chat/opponent-message";
    public const string InventorySlotTemplate = "ui/template/inventory/slot";
}

// 启动期一次性预加载 UI 契约。Catalog 拥有 Addressables handles；窗口只克隆
// VisualTree，在关闭时移除实例，不在输入回调中触发异步加载。
public sealed class UiContentCatalog : IDisposable
{
    private static readonly string[] VisualTreeAddresses =
    {
        UiContentIds.GameplayHud,
        UiContentIds.ChatWindow,
        UiContentIds.InventoryListWindow,
        UiContentIds.InventoryInteractWindow,
        UiContentIds.InventoryWindow,
        UiContentIds.ItemDispenserWindow,
        UiContentIds.SaveGameWindow,
        UiContentIds.SystemMessageTemplate,
        UiContentIds.PlayerMessageTemplate,
        UiContentIds.OpponentMessageTemplate,
        UiContentIds.InventorySlotTemplate
    };

    private readonly IContentAssetProvider _provider;
    private readonly Dictionary<string, ContentAssetLease<VisualTreeAsset>> _visualTrees =
        new Dictionary<string, ContentAssetLease<VisualTreeAsset>>(StringComparer.Ordinal);
    private ContentAssetLease<PanelSettings> _panelSettings;
    private bool _disposed;

    public UiContentCatalog(IContentAssetProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public bool IsReady { get; private set; }
    public PanelSettings PanelSettings => IsReady ? _panelSettings?.Asset : null;

    public async Task PreloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsReady)
            return;

        try
        {
            _panelSettings = await _provider.LoadAssetAsync<PanelSettings>(
                UiContentIds.PanelSettings,
                cancellationToken);
            if (_panelSettings.Asset == null)
                throw new InvalidOperationException("UI PanelSettings resolved to null.");

            foreach (string address in VisualTreeAddresses)
            {
                ContentAssetLease<VisualTreeAsset> lease =
                    await _provider.LoadAssetAsync<VisualTreeAsset>(address, cancellationToken);
                if (lease.Asset == null)
                {
                    lease.Dispose();
                    throw new InvalidOperationException($"UI asset '{address}' resolved to null.");
                }
                _visualTrees.Add(address, lease);
            }

            UiContentContractValidator.Validate(this);
            IsReady = true;
        }
        catch
        {
            ReleaseLeases();
            throw;
        }
    }

    public VisualTreeAsset GetVisualTree(string address)
    {
        ThrowIfDisposed();
        if (!IsReady)
            throw new InvalidOperationException("UI content catalog is not ready.");
        if (string.IsNullOrWhiteSpace(address) ||
            !_visualTrees.TryGetValue(address, out ContentAssetLease<VisualTreeAsset> lease) ||
            lease.Asset == null)
            throw new KeyNotFoundException($"UI content address '{address}' is not loaded.");
        return lease.Asset;
    }

    internal VisualTreeAsset GetVisualTreeForValidation(string address)
    {
        if (_visualTrees.TryGetValue(address, out ContentAssetLease<VisualTreeAsset> lease))
            return lease.Asset;
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseLeases();
    }

    private void ReleaseLeases()
    {
        IsReady = false;
        foreach (ContentAssetLease<VisualTreeAsset> lease in _visualTrees.Values)
            lease.Dispose();
        _visualTrees.Clear();
        _panelSettings?.Dispose();
        _panelSettings = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UiContentCatalog));
    }
}

public static class UiContentContractValidator
{
    private readonly struct RequiredElement
    {
        public RequiredElement(string name, Type type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public Type Type { get; }
    }

    private static readonly IReadOnlyDictionary<string, RequiredElement[]> Contracts =
        new Dictionary<string, RequiredElement[]>(StringComparer.Ordinal)
        {
            [UiContentIds.ChatWindow] = new[]
            {
                E<ScrollView>("chat-scroll"), E<TextField>("chat-input"),
                E<Button>("send-button"), E<Button>("close-button"),
                E<Label>("chat-placeholder"), E<Label>("character-count"),
                E<Label>("chat-title"), E<VisualElement>("npc-list-container")
            },
            [UiContentIds.InventoryListWindow] = new[]
            {
                E<ScrollView>("inv-list-scroll"), E<VisualElement>("inv-list-container")
            },
            [UiContentIds.InventoryInteractWindow] = new[]
            {
                E<VisualElement>("inv-grid-2"), E<VisualElement>("inv-grid-1"),
                E<Label>("inv-section-label-2"), E<Label>("inv-section-label-1")
            },
            [UiContentIds.InventoryWindow] = new[]
            {
                E<VisualElement>("inv-grid-1"), E<Label>("inv-panel-title")
            },
            [UiContentIds.ItemDispenserWindow] = new[]
            {
                E<VisualElement>("dispenser-container-grid"),
                E<VisualElement>("dispenser-catalog-grid"),
                E<VisualElement>("dispenser-list-container"),
                E<Label>("dispenser-info-name"), E<Label>("dispenser-info-desc"),
                E<Label>("dispenser-info-id"),
                E<SliderInt>("dispenser-quantity-slider"),
                E<Button>("dispenser-dispense-btn")
            },
            [UiContentIds.SaveGameWindow] = new[]
            {
                E<TextField>("save-name"), E<VisualElement>("save-list"),
                E<Label>("save-status"), E<Button>("save-create"),
                E<Button>("save-overwrite"), E<Button>("save-retry"),
                E<Button>("save-load"), E<Button>("save-close")
            },
            [UiContentIds.SystemMessageTemplate] = new[] { E<Label>("message-text") },
            [UiContentIds.PlayerMessageTemplate] = new[] { E<Label>("message-text") },
            [UiContentIds.OpponentMessageTemplate] = new[]
            {
                E<Label>("message-text"), E<Label>("avatar-label")
            },
            [UiContentIds.InventorySlotTemplate] = new[]
            {
                E<VisualElement>("slot-icon"), E<Label>("slot-quantity")
            }
        };

    public static void Validate(UiContentCatalog catalog)
    {
        if (catalog == null)
            throw new ArgumentNullException(nameof(catalog));
        foreach (KeyValuePair<string, RequiredElement[]> contract in Contracts)
        {
            VisualTreeAsset asset = catalog.GetVisualTreeForValidation(contract.Key);
            ValidateAsset(contract.Key, asset);
        }
    }

    public static void ValidateAsset(string address, VisualTreeAsset asset)
    {
        if (!Contracts.TryGetValue(address, out RequiredElement[] requiredElements))
            return;
        if (asset == null)
            throw new InvalidOperationException($"UI contract asset '{address}' is missing.");
        TemplateContainer root = asset.CloneTree();
        foreach (RequiredElement required in requiredElements)
        {
            VisualElement element = root.Q<VisualElement>(required.Name);
            if (element == null || !required.Type.IsInstanceOfType(element))
            {
                string actual = element == null ? "missing" : element.GetType().Name;
                throw new InvalidOperationException(
                    $"UI contract '{address}' requires '{required.Name}' as " +
                    $"{required.Type.Name}, actual: {actual}.");
            }
        }
    }

    private static RequiredElement E<T>(string name) where T : VisualElement =>
        new RequiredElement(name, typeof(T));
}
