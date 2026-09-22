using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class ClientTextCatalog
{
    private const int MaxTextLength = 2048;
    private readonly ReadOnlyDictionary<string, string> _texts;

    public int SchemaVersion { get; }
    public string ContentVersion { get; }
    public string Locale { get; }
    public IReadOnlyDictionary<string, string> Texts => _texts;

    private ClientTextCatalog(CatalogDto dto)
    {
        SchemaVersion = dto.schemaVersion;
        ContentVersion = dto.contentVersion;
        Locale = dto.locale;
        _texts = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(dto.texts, StringComparer.Ordinal));
    }

    public static ClientTextCatalog Parse(string json, string expectedVersion, string expectedLocale = "zh-CN")
    {
        CatalogDto dto;
        try
        {
            JToken parsed = JToken.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore
            });
            if (!(parsed is JObject))
                throw new JsonSerializationException("Catalog root must be a JSON object.");
            dto = parsed.ToObject<CatalogDto>(JsonSerializer.Create(
                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Client text Catalog JSON is invalid: {ex.Message}", ex);
        }
        if (dto == null || dto.schemaVersion != 1)
            throw new InvalidOperationException("Client text Catalog schemaVersion must be 1.");
        ValidateIdentifier(dto.contentVersion, "contentVersion");
        ValidateIdentifier(dto.locale, "locale");
        if (!string.Equals(dto.contentVersion, expectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Client text Catalog contentVersion does not match the release.");
        if (!string.Equals(dto.locale, expectedLocale, StringComparison.Ordinal))
            throw new InvalidOperationException($"Client text Catalog locale must be '{expectedLocale}'.");
        if (dto.texts == null || dto.texts.Count == 0)
            throw new InvalidOperationException("Client text Catalog texts object cannot be empty.");
        foreach (KeyValuePair<string, string> pair in dto.texts)
        {
            ValidateKey(pair.Key);
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > MaxTextLength ||
                !string.Equals(pair.Value, pair.Value.Trim(), StringComparison.Ordinal) || pair.Value.Any(char.IsControl))
                throw new InvalidOperationException($"Client text '{pair.Key}' is invalid.");
            try
            {
                _ = string.Format(
                    CultureInfo.InvariantCulture,
                    pair.Value,
                    new object[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Client text '{pair.Key}' has invalid format placeholders.", ex);
            }
        }
        return new ClientTextCatalog(dto);
    }

    public string Get(string key)
    {
        if (!_texts.TryGetValue(key, out string value))
            throw new KeyNotFoundException($"Client text key '{key}' is not present in Catalog '{ContentVersion}'.");
        return value;
    }

    private static void ValidateIdentifier(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new InvalidOperationException($"Client text Catalog {field} is invalid.");
    }

    private static void ValidateKey(string key)
    {
        ValidateIdentifier(key, "text key");
        if (!char.IsLower(key[0]) || key.Any(character =>
                !(character >= 'a' && character <= 'z') &&
                !(character >= '0' && character <= '9') && character != '.' && character != '_'))
            throw new InvalidOperationException($"Client text key '{key}' must use lowercase stable segments.");
    }

    private sealed class CatalogDto
    {
        public int schemaVersion;
        public string contentVersion;
        public string locale;
        public Dictionary<string, string> texts;
    }
}

public static class ClientTextCatalogs
{
    private static ClientTextCatalog _agentMessages;
    private static ClientTextCatalog _ui;

    public static ClientTextCatalog AgentMessages => Volatile.Read(ref _agentMessages);
    public static ClientTextCatalog UI => Volatile.Read(ref _ui);

    public static void Activate(ClientTextCatalog agentMessages, ClientTextCatalog ui)
    {
        if (agentMessages == null)
            throw new ArgumentNullException(nameof(agentMessages));
        if (ui == null)
            throw new ArgumentNullException(nameof(ui));
        Volatile.Write(ref _agentMessages, agentMessages);
        Volatile.Write(ref _ui, ui);
    }

    public static string Message(string key, string fallback, params object[] arguments) =>
        Resolve(AgentMessages, key, fallback, arguments);

    public static string UiText(string key, string fallback, params object[] arguments) =>
        Resolve(UI, key, fallback, arguments);

    private static string Resolve(
        ClientTextCatalog catalog,
        string key,
        string fallback,
        object[] arguments)
    {
        string template = fallback;
        if (catalog != null && catalog.Texts.TryGetValue(key, out string value))
            template = value;
        return arguments == null || arguments.Length == 0
            ? template
            : string.Format(CultureInfo.InvariantCulture, template, arguments);
    }
}
