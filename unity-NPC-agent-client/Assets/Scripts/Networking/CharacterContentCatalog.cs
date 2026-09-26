using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class CharacterContentIds
{
    public const string Catalog = "character/catalog/default";
}

public sealed class CharacterAppearanceDefinition
{
    public string CharacterId { get; internal set; }
    public string AppearanceId { get; internal set; }
    public string PrefabAddress { get; internal set; }
    public string AvatarAddress { get; internal set; }
    public string AnimatorControllerAddress { get; internal set; }
    public IReadOnlyList<string> AnimationSetAddresses { get; internal set; }
    public IReadOnlyList<string> MaterialAddresses { get; internal set; }
    public IReadOnlyList<string> TextureAddresses { get; internal set; }
}

// 角色目录只保存稳定业务 ID 与逻辑地址，不直接引用任何角色资源。
// 因此加载目录不会隐式下载所有模型，具体 Prefab 由实体 VisualRoot 按需实例化。
public sealed class CharacterContentCatalog : IDisposable
{
    private readonly IContentAssetProvider _provider;
    private ContentAssetLease<TextAsset> _catalogLease;
    private IReadOnlyDictionary<string, CharacterAppearanceDefinition> _appearances;
    private bool _disposed;

    public CharacterContentCatalog(IContentAssetProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public bool IsReady { get; private set; }

    public async Task PreloadAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (IsReady)
            return;

        try
        {
            _catalogLease = await _provider.LoadAssetAsync<TextAsset>(
                CharacterContentIds.Catalog,
                cancellationToken);
            _appearances = Parse(_catalogLease.Asset == null ? null : _catalogLease.Asset.text);
            IsReady = true;
        }
        catch
        {
            ReleaseCatalog();
            throw;
        }
    }

    public CharacterAppearanceDefinition Get(string characterId, string appearanceId)
    {
        ThrowIfDisposed();
        if (!IsReady)
            throw new InvalidOperationException("Character content Catalog is not ready.");
        string key = Key(characterId, appearanceId);
        if (!_appearances.TryGetValue(key, out CharacterAppearanceDefinition definition))
            throw new KeyNotFoundException(
                $"Character appearance '{characterId}/{appearanceId}' is not published.");
        return definition;
    }

    public Task<ContentInstanceLease> InstantiateAsync(
        string characterId,
        string appearanceId,
        Transform visualRoot,
        CancellationToken cancellationToken)
    {
        if (visualRoot == null)
            throw new ArgumentNullException(nameof(visualRoot));
        CharacterAppearanceDefinition definition = Get(characterId, appearanceId);
        return _provider.InstantiateAsync(definition.PrefabAddress, visualRoot, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseCatalog();
    }

    public static IReadOnlyDictionary<string, CharacterAppearanceDefinition> Parse(string json)
    {
        JObject root;
        try
        {
            root = JObject.Parse(json ?? string.Empty);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Character content Catalog JSON is invalid.", ex);
        }

        if ((int?)root["schemaVersion"] != 1 ||
            !IsVersion((string)root["contentVersion"]) ||
            !(root["appearances"] is JArray source) || source.Count == 0)
            throw new InvalidOperationException("Character content Catalog header is invalid.");

        var result = new Dictionary<string, CharacterAppearanceDefinition>(StringComparer.Ordinal);
        foreach (JToken token in source)
        {
            if (!(token is JObject item))
                throw new InvalidOperationException("Character appearance entry must be an object.");
            string characterId = (string)item["characterId"];
            string appearanceId = (string)item["appearanceId"];
            string prefix = $"character/{characterId}";
            string prefabAddress = (string)item["prefabAddress"];
            string avatarAddress = OptionalString(item["avatarAddress"]);
            string animatorAddress = OptionalString(item["animatorControllerAddress"]);
            IReadOnlyList<string> animations = StringArray(item["animationSetAddresses"]);
            IReadOnlyList<string> materials = StringArray(item["materialAddresses"]);
            IReadOnlyList<string> textures = StringArray(item["textureAddresses"]);

            if (!IsStableId(characterId) || !IsStableId(appearanceId) ||
                prefabAddress != $"{prefix}/visual/{appearanceId}" ||
                !OptionalAddress(avatarAddress, prefix + "/avatar/") ||
                string.IsNullOrEmpty(animatorAddress) ||
                !OptionalAddress(animatorAddress, prefix + "/animator/") ||
                animations.Count == 0 || materials.Count == 0 || textures.Count == 0 ||
                !AllAddresses(animations, prefix + "/animation/") ||
                !AllAddresses(materials, prefix + "/material/") ||
                !AllAddresses(textures, prefix + "/texture/"))
                throw new InvalidOperationException(
                    $"Character appearance '{characterId}/{appearanceId}' has an invalid mapping.");

            var definition = new CharacterAppearanceDefinition
            {
                CharacterId = characterId,
                AppearanceId = appearanceId,
                PrefabAddress = prefabAddress,
                AvatarAddress = avatarAddress,
                AnimatorControllerAddress = animatorAddress,
                AnimationSetAddresses = animations,
                MaterialAddresses = materials,
                TextureAddresses = textures
            };
            if (!result.TryAdd(Key(characterId, appearanceId), definition))
                throw new InvalidOperationException(
                    $"Duplicate character appearance '{characterId}/{appearanceId}'.");
        }
        return result;
    }

    private static IReadOnlyList<string> StringArray(JToken token)
    {
        if (!(token is JArray values))
            return Array.Empty<string>();
        string[] result = values.Select(value => value.Type == JTokenType.String ? (string)value : null)
            .ToArray();
        return result.Any(string.IsNullOrWhiteSpace) || result.Distinct(StringComparer.Ordinal).Count() != result.Length
            ? Array.Empty<string>()
            : result;
    }

    private static string OptionalString(JToken token) =>
        token == null || token.Type == JTokenType.Null ? null :
        token.Type == JTokenType.String ? (string)token : string.Empty;

    private static bool OptionalAddress(string value, string prefix) =>
        value == null || value.StartsWith(prefix, StringComparison.Ordinal);

    private static bool AllAddresses(IEnumerable<string> values, string prefix) =>
        values.All(value => value.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Any(char.IsControl);

    public static bool IsStableId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character =>
            character >= 'a' && character <= 'z' ||
            character >= '0' && character <= '9' || character == '-');

    private static string Key(string characterId, string appearanceId) =>
        (characterId ?? string.Empty) + "/" + (appearanceId ?? string.Empty);

    private void ReleaseCatalog()
    {
        IsReady = false;
        _appearances = null;
        _catalogLease?.Dispose();
        _catalogLease = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CharacterContentCatalog));
    }
}
