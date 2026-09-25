using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.U2D;

public static class ItemContentIds
{
    public const string Catalog = "item/catalog/default";
    public const string TextCatalog = "item/catalog/text/zh-CN";
}

// A4 的运行时边界：启动期加载目录与 UI 图标，不加载世界模型。
// Catalog 独占所有 lease，因此 Inventory 只同步读取已验证的缓存。
public sealed class ItemContentCatalog : IDisposable
{
    private readonly IContentAssetProvider _provider;
    private readonly List<ContentAssetLease<SpriteAtlas>> _atlasLeases =
        new List<ContentAssetLease<SpriteAtlas>>();
    private readonly List<Sprite> _loadedIcons = new List<Sprite>();
    private ContentAssetLease<ItemDataList> _catalogLease;
    private ContentAssetLease<TextAsset> _textCatalogLease;
    private bool _disposed;

    public ItemContentCatalog(IContentAssetProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public bool IsReady { get; private set; }
    public ItemDataList Items => IsReady ? _catalogLease?.Asset : null;

    public async Task PreloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsReady)
            return;

        try
        {
            _catalogLease = await _provider.LoadAssetAsync<ItemDataList>(
                ItemContentIds.Catalog,
                cancellationToken);
            ItemDataList catalog = _catalogLease.Asset;
            Validate(catalog);
            _textCatalogLease = await _provider.LoadAssetAsync<TextAsset>(
                ItemContentIds.TextCatalog,
                cancellationToken);
            IReadOnlyDictionary<string, string> texts = ParsePresentationTexts(
                _textCatalogLease.Asset == null ? null : _textCatalogLease.Asset.text);
            foreach (ItemData item in catalog.items)
            {
                if (!texts.TryGetValue(item.DisplayNameKey, out string itemName) ||
                    !texts.TryGetValue(item.DescriptionKey, out string description))
                    throw new InvalidOperationException(
                        $"Item presentation Catalog is missing text keys for '{item.ItemId}'.");
                item.SetLoadedPresentation(itemName, description);
            }

            foreach (IGrouping<string, ItemData> family in catalog.items.GroupBy(
                         item => item.IconAtlasAddress,
                         StringComparer.Ordinal))
            {
                ContentAssetLease<SpriteAtlas> lease = await _provider.LoadAssetAsync<SpriteAtlas>(
                    family.Key,
                    cancellationToken);
                if (lease.Asset == null)
                {
                    lease.Dispose();
                    throw new InvalidOperationException(
                        $"Item icon atlas '{family.Key}' resolved to null.");
                }
                _atlasLeases.Add(lease);
                foreach (ItemData item in family)
                {
                    Sprite icon = lease.Asset.GetSprite(item.IconName);
                    if (icon == null)
                        throw new InvalidOperationException(
                            $"Item '{item.ItemId}' icon '{item.IconName}' is missing from atlas '{family.Key}'.");
                    _loadedIcons.Add(icon);
                    item.SetLoadedIcon(icon);
                }
            }
            IsReady = true;
        }
        catch
        {
            ReleaseLeases();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseLeases();
    }

    public static void Validate(ItemDataList catalog)
    {
        if (catalog?.items == null || catalog.items.Count == 0)
            throw new InvalidOperationException("Item catalog is missing or empty.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var iconKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ItemData item in catalog.items)
        {
            if (item == null || !IsStableId(item.ItemId) || !ids.Add(item.ItemId))
                throw new InvalidOperationException("Item catalog contains a missing, invalid, or duplicate itemId.");
            if (item.MaxStackSize <= 0)
                throw new InvalidOperationException($"Item '{item.ItemId}' has an invalid MaxStackSize.");
            if (item.DisplayNameKey != $"item.{item.ItemId}.name" ||
                item.DescriptionKey != $"item.{item.ItemId}.description")
                throw new InvalidOperationException($"Item '{item.ItemId}' has invalid text keys.");
            if (item.IconAtlasAddress != "item/icons/core-atlas" ||
                string.IsNullOrWhiteSpace(item.IconName) ||
                !iconKeys.Add(item.IconAtlasAddress + "/" + item.IconName))
                throw new InvalidOperationException($"Item '{item.ItemId}' has an invalid or duplicate icon mapping.");
            if (!string.IsNullOrEmpty(item.WorldVisualAddress) &&
                item.WorldVisualAddress != $"item/{item.ItemId}/world-visual")
                throw new InvalidOperationException($"Item '{item.ItemId}' has an invalid world visual address.");
        }
    }

    public static IReadOnlyDictionary<string, string> ParsePresentationTexts(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json ?? string.Empty);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Item presentation Catalog JSON is invalid.", ex);
        }
        string contentVersion = (string)root["contentVersion"];
        if ((int?)root["schemaVersion"] != 1 || (string)root["locale"] != "zh-CN" ||
            string.IsNullOrWhiteSpace(contentVersion) || contentVersion != contentVersion.Trim() ||
            !(root["texts"] is JObject source))
            throw new InvalidOperationException("Item presentation Catalog header is invalid.");
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JProperty property in source.Properties())
        {
            string value = property.Value.Type == JTokenType.String ? (string)property.Value : null;
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) ||
                !texts.TryAdd(property.Name, value))
                throw new InvalidOperationException(
                    $"Item presentation text '{property.Name}' is invalid.");
        }
        return texts;
    }

    private static bool IsStableId(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(character =>
            character >= 'a' && character <= 'z' ||
            character >= '0' && character <= '9' || character == '-');

    private void ReleaseLeases()
    {
        IsReady = false;
        if (_catalogLease?.Asset?.items != null)
        {
            foreach (ItemData item in _catalogLease.Asset.items)
            {
                item?.SetLoadedIcon(null);
                item?.SetLoadedPresentation(null, null);
            }
        }
        foreach (ContentAssetLease<SpriteAtlas> lease in _atlasLeases)
            lease.Dispose();
        _atlasLeases.Clear();
        foreach (Sprite icon in _loadedIcons)
        {
            if (icon == null)
                continue;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(icon);
            else
                UnityEngine.Object.DestroyImmediate(icon);
        }
        _loadedIcons.Clear();
        _catalogLease?.Dispose();
        _catalogLease = null;
        _textCatalogLease?.Dispose();
        _textCatalogLease = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ItemContentCatalog));
    }
}
