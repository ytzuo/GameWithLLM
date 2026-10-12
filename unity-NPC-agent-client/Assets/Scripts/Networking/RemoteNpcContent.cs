using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Networking;
using UnityEngine.ResourceManagement.ResourceLocations;

public sealed class RemoteNpcCatalog
{
    public int SchemaVersion { get; internal set; }
    public string CatalogContentVersion { get; internal set; }
    public IReadOnlyDictionary<string, string> Texts { get; internal set; }
    public IReadOnlyList<RemoteNpcSummary> Npcs { get; internal set; }
}

public sealed class RemoteNpcSummary
{
    public string NpcId { get; internal set; }
    public string ContentVersion { get; internal set; }
    public string DisplayName { get; internal set; }
    public string Description { get; internal set; }
    public string AvatarPath { get; internal set; }
    public string ManifestPath { get; internal set; }
    public string ManifestSha256 { get; internal set; }
    public string MinPlayerVersion { get; internal set; }
    public string MaxPlayerVersion { get; internal set; }

    public static RemoteNpcSummary FromInstalled(InstalledNpcRecord record)
    {
        if (record == null)
            throw new ArgumentNullException(nameof(record));
        return new RemoteNpcSummary
        {
            NpcId = record.NpcId,
            ContentVersion = record.ContentVersion,
            DisplayName = record.DisplayName,
            Description = record.Description,
            AvatarPath = record.AvatarPath,
            ManifestSha256 = record.ManifestSha256
        };
    }
}

public sealed class RemoteNpcManifest
{
    public int SchemaVersion { get; internal set; }
    public string NpcId { get; internal set; }
    public string ContentVersion { get; internal set; }
    public string PlayerBuildId { get; internal set; }
    public string PrefabAddress { get; internal set; }
    public string AnimatorControllerAddress { get; internal set; }
    public IReadOnlyList<string> AnimationAddresses { get; internal set; }
    public IReadOnlyList<string> MaterialAddresses { get; internal set; }
    public IReadOnlyList<string> TextureAddresses { get; internal set; }
    public string AnimationScriptAddress { get; internal set; }
    public string AnimationAssemblyName { get; internal set; }
    public string AnimationEntryType { get; internal set; }
    public long AnimationLength { get; internal set; }
    public string AnimationSha256 { get; internal set; }
    public string AnimationDriverKind { get; internal set; }
    public string BuiltinAnimationDriverId { get; internal set; }
    public string RawJson { get; internal set; }

    public bool UsesBuiltinAnimationDriver =>
        string.Equals(AnimationDriverKind, "builtin", StringComparison.Ordinal);

    public IReadOnlyList<string> DownloadAddresses => SchemaVersion == 1
        ? new[] { PrefabAddress, AnimatorControllerAddress }
            .Concat(AnimationAddresses).Concat(MaterialAddresses).Concat(TextureAddresses)
            .Append(AnimationScriptAddress).Distinct(StringComparer.Ordinal).ToArray()
        : new[] { PrefabAddress }
            .Concat(UsesBuiltinAnimationDriver ? Array.Empty<string>() : new[] { AnimationScriptAddress })
            .Distinct(StringComparer.Ordinal).ToArray();
}

public static class RemoteNpcContract
{
    private static readonly Regex StableId = new Regex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ContentVersion = new Regex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex PlayerVersion = new Regex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new Regex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] TextKeys =
    {
        "remoteTab", "localTab", "download", "spawn", "cancel", "retry", "close", "refresh",
        "alreadySpawned", "restartRequired", "progressFormat", "downloading", "installed",
        "remoteEmpty", "localEmpty", "errorFetch", "errorContent", "errorCompatibility",
        "errorDownload", "errorCancelled", "errorSpawn", "errorBusy"
    };

    public static RemoteNpcCatalog ParseCatalog(string json)
    {
        JObject root = ParseObject(json, 256 * 1024);
        Exact(root, "schemaVersion", "catalogContentVersion", "texts", "npcs");
        RequireInt(root, "schemaVersion", 1);
        string catalogVersion = String(root, "catalogContentVersion", 1, 128);
        JObject texts = Object(root, "texts");
        Exact(texts, TextKeys);
        var textMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in TextKeys)
        {
            string value = PlainText(texts, key, 1, 2048);
            if (Encoding.UTF8.GetByteCount(value) > 2048)
                throw new InvalidDataException($"Text '{key}' exceeds the UTF-8 byte limit.");
            textMap.Add(key, value);
        }
        if (!textMap["progressFormat"].Contains("{downloaded}", StringComparison.Ordinal) ||
            !textMap["progressFormat"].Contains("{total}", StringComparison.Ordinal))
            throw new InvalidDataException("progressFormat must contain both byte placeholders.");

        JArray npcs = Array(root, "npcs");
        if (npcs.Count > 256)
            throw new InvalidDataException("NPC catalog exceeds the entry limit.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RemoteNpcSummary>(npcs.Count);
        foreach (JToken token in npcs)
        {
            if (!(token is JObject item)) throw new InvalidDataException("NPC catalog item must be an object.");
            Exact(item, "npcId", "contentVersion", "displayName", "description", "avatarPath",
                "manifestPath", "manifestSha256", "minPlayerVersion", "maxPlayerVersion");
            string npcId = Stable(String(item, "npcId", 1, 64), StableId, "npcId");
            string contentVersion = Stable(String(item, "contentVersion", 1, 64), ContentVersion, "contentVersion");
            if (!ids.Add(npcId)) throw new InvalidDataException($"Duplicate NPC '{npcId}'.");
            string expectedPrefix = $"npc/{npcId}/{contentVersion}/";
            string avatarPath = TrustedPath(String(item, "avatarPath", 1, 256), expectedPrefix, "avatar.png");
            string manifestPath = TrustedPath(String(item, "manifestPath", 1, 256), expectedPrefix, "npc.json");
            result.Add(new RemoteNpcSummary
            {
                NpcId = npcId, ContentVersion = contentVersion,
                DisplayName = PlainText(item, "displayName", 1, 80),
                Description = PlainText(item, "description", 1, 600),
                AvatarPath = avatarPath, ManifestPath = manifestPath,
                ManifestSha256 = Stable(String(item, "manifestSha256", 64, 64), Sha256, "manifestSha256"),
                MinPlayerVersion = Stable(String(item, "minPlayerVersion", 1, 64), PlayerVersion, "minPlayerVersion"),
                MaxPlayerVersion = Stable(String(item, "maxPlayerVersion", 1, 64), PlayerVersion, "maxPlayerVersion")
            });
        }
        return new RemoteNpcCatalog { SchemaVersion = 1, CatalogContentVersion = catalogVersion, Texts = textMap, Npcs = result };
    }

    public static RemoteNpcManifest ParseManifest(string json, RemoteNpcSummary expected = null)
    {
        JObject root = ParseObject(json, 256 * 1024);
        int schemaVersion = checked((int)Long(root, "schemaVersion", 1, 2));
        if (schemaVersion == 1)
            Exact(root, "schemaVersion", "npcId", "contentVersion", "playerBuildId", "visual", "animationScript", "profile", "systemPrompt");
        else
            Exact(root, "schemaVersion", "npcId", "contentVersion", "playerBuildId", "visual", "animationDriver", "profile", "systemPrompt");
        string npcId = Stable(String(root, "npcId", 1, 64), StableId, "npcId");
        string version = Stable(String(root, "contentVersion", 1, 64), ContentVersion, "contentVersion");
        if (expected != null && (npcId != expected.NpcId || version != expected.ContentVersion))
            throw new InvalidDataException("NPC manifest identity does not match its catalog entry.");

        JObject visual = Object(root, "visual");
        string prefix = $"npc/{npcId}/{version}/";
        JObject profile = Object(root, "profile");
        Exact(profile, "npcId", "displayName", "personality", "speakingStyle", "identity", "responsibilities", "worldKnowledge", "forbiddenTopics");
        if (String(profile, "npcId", 1, 64) != npcId) throw new InvalidDataException("Profile npcId mismatch.");
        PlainText(profile, "displayName", 1, 80); PlainText(profile, "speakingStyle", 1, 2000); PlainText(profile, "identity", 1, 300);
        ValidatePlainArray(profile, "personality", 1, 64, 1000);
        ValidatePlainArray(profile, "responsibilities", 1, 64, 1000);
        ValidatePlainArray(profile, "worldKnowledge", 1, 64, 1000);
        ValidatePlainArray(profile, "forbiddenTopics", 1, 64, 1000);
        JObject prompt = Object(root, "systemPrompt");
        Exact(prompt, "schemaVersion", "contentVersion", "locale", "template");
        RequireInt(prompt, "schemaVersion", 1);
        if (String(prompt, "contentVersion", 1, 64) != version || String(prompt, "locale", 1, 32) != "zh-CN")
            throw new InvalidDataException("System prompt identity is invalid.");
        String(prompt, "template", 1, 64 * 1024);

        var result = new RemoteNpcManifest
        {
            SchemaVersion = schemaVersion, NpcId = npcId, ContentVersion = version,
            PlayerBuildId = String(root, "playerBuildId", 1, 128),
            PrefabAddress = Address(visual, "prefabAddress", prefix),
            AnimationAddresses = System.Array.Empty<string>(),
            MaterialAddresses = System.Array.Empty<string>(),
            TextureAddresses = System.Array.Empty<string>(),
            RawJson = root.ToString(Formatting.None)
        };
        if (schemaVersion == 1)
        {
            Exact(visual, "prefabAddress", "animatorControllerAddress", "animationAddresses", "materialAddresses", "textureAddresses");
            JObject script = Object(root, "animationScript");
            Exact(script, "address", "assemblyName", "entryType", "length", "sha256");
            result.AnimatorControllerAddress = Address(visual, "animatorControllerAddress", prefix);
            result.AnimationAddresses = AddressArray(visual, "animationAddresses", prefix);
            result.MaterialAddresses = AddressArray(visual, "materialAddresses", prefix);
            result.TextureAddresses = AddressArray(visual, "textureAddresses", prefix);
            ReadHotUpdateDriver(script, prefix, result);
        }
        else
        {
            Exact(visual, "prefabAddress");
            JObject driver = Object(root, "animationDriver");
            string kind = String(driver, "kind", 1, 32);
            if (kind == "builtin")
            {
                Exact(driver, "kind", "driverId");
                string driverId = String(driver, "driverId", 1, 64);
                if (!string.Equals(driverId, StandardLocomotionAnimationDriver.DriverId, StringComparison.Ordinal))
                    throw new InvalidDataException("Unknown builtin NPC animation driver.");
                result.AnimationDriverKind = kind;
                result.BuiltinAnimationDriverId = driverId;
            }
            else if (kind == "hotUpdate")
            {
                Exact(driver, "kind", "address", "assemblyName", "entryType", "length", "sha256");
                ReadHotUpdateDriver(driver, prefix, result);
            }
            else throw new InvalidDataException("Unknown NPC animation driver kind.");
        }
        return result;
    }

    private static void ReadHotUpdateDriver(JObject script, string prefix, RemoteNpcManifest result)
    {
        result.AnimationDriverKind = "hotUpdate";
        result.AnimationScriptAddress = Address(script, "address", prefix);
        result.AnimationAssemblyName = String(script, "assemblyName", 1, 160);
        result.AnimationEntryType = String(script, "entryType", 1, 240);
        result.AnimationLength = Long(script, "length", 1, 16 * 1024 * 1024);
        result.AnimationSha256 = Stable(String(script, "sha256", 64, 64), Sha256, "animation sha256");
    }

    public static string ComputeSha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2")));
    }

    private static JObject ParseObject(string json, int maxChars)
    {
        if (string.IsNullOrEmpty(json) || json.Length > maxChars) throw new InvalidDataException("JSON size is invalid.");
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        try { return JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); }
        catch (JsonException ex) { throw new InvalidDataException("JSON is malformed or contains duplicate fields.", ex); }
    }

    private static void Exact(JObject obj, params string[] allowed)
    {
        var expected = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (JProperty property in obj.Properties()) if (!expected.Remove(property.Name)) throw new InvalidDataException($"Unknown field '{property.Name}'.");
        if (expected.Count != 0) throw new InvalidDataException($"Missing field '{expected.First()}'.");
    }
    private static JObject Object(JObject o, string n) => o[n] as JObject ?? throw new InvalidDataException($"'{n}' must be an object.");
    private static JArray Array(JObject o, string n) => o[n] as JArray ?? throw new InvalidDataException($"'{n}' must be an array.");
    private static string String(JObject o, string n, int min, int max) { if (o[n]?.Type != JTokenType.String) throw new InvalidDataException($"'{n}' must be a string."); string s = (string)o[n]; if (s.Length < min || s.Length > max || s.IndexOf('\0') >= 0) throw new InvalidDataException($"'{n}' length is invalid."); return s; }
    private static string PlainText(JObject o, string n, int min, int max) { string s = String(o, n, min, max); if (s.Contains("<") || s.Contains(">")) throw new InvalidDataException($"'{n}' must be plain text."); return s; }
    private static long Long(JObject o, string n, long min, long max) { if (o[n]?.Type != JTokenType.Integer) throw new InvalidDataException($"'{n}' must be an integer."); long v = (long)o[n]; if (v < min || v > max) throw new InvalidDataException($"'{n}' is out of range."); return v; }
    private static void RequireInt(JObject o, string n, int expected) { if (Long(o, n, expected, expected) != expected) throw new InvalidDataException($"'{n}' is unsupported."); }
    private static string Stable(string value, Regex regex, string name) => regex.IsMatch(value) ? value : throw new InvalidDataException($"'{name}' is invalid.");
    private static string TrustedPath(string p, string prefix, string suffix) { if (!string.Equals(p, prefix + suffix, StringComparison.Ordinal) || p.Contains("%") || p.Contains("?") || p.Contains("#") || p.Contains("\\")) throw new InvalidDataException("Content path escapes the trusted root."); return p; }
    private static string Address(JObject o, string n, string prefix) { string v = String(o, n, 1, 256); if (!v.StartsWith(prefix, StringComparison.Ordinal) || v.Length == prefix.Length || v.Contains("..") || v.Contains(":") || v.Contains("\\") || v.Contains("%") || v.Contains("?") || v.Contains("#") || v.Contains("//")) throw new InvalidDataException($"'{n}' is outside the NPC namespace."); return v; }
    private static IReadOnlyList<string> AddressArray(JObject o, string n, string prefix) { JArray a = Array(o, n); if (a.Count > 128) throw new InvalidDataException($"'{n}' has too many entries."); return a.Select(x => x.Type == JTokenType.String ? (string)x : throw new InvalidDataException($"'{n}' contains a non-string.")).Select(x => { var wrapper = new JObject { [n] = x }; return Address(wrapper, n, prefix); }).Distinct(StringComparer.Ordinal).ToArray(); }
    private static void ValidatePlainArray(JObject o, string n, int min, int max, int itemMax) { JArray a = Array(o, n); if (a.Count < min || a.Count > max) throw new InvalidDataException($"'{n}' count is invalid."); foreach (JToken t in a) { if (t.Type != JTokenType.String || ((string)t).Length == 0 || ((string)t).Length > itemMax || ((string)t).Contains("<") || ((string)t).Contains(">")) throw new InvalidDataException($"'{n}' contains invalid text."); } }
}

public sealed class RemoteNpcCatalogClient : IDisposable
{
    private readonly Uri _root;
    private readonly string _cachePath;
    private readonly TimeSpan _timeout;
    private readonly List<UnityEngine.Object> _avatarAssets = new List<UnityEngine.Object>();

    public RemoteNpcCatalogClient(Uri root, string storageRoot, TimeSpan timeout)
    {
        if (root == null || !root.IsAbsoluteUri || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps)) throw new ArgumentException("NPC content root must be trusted HTTP(S).", nameof(root));
        _root = root.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? root : new Uri(root.AbsoluteUri + "/");
        _cachePath = Path.Combine(storageRoot, "catalog-cache.json"); _timeout = timeout;
    }

    public async Task<RemoteNpcCatalog> FetchAsync(CancellationToken token)
    {
        try
        {
            byte[] bytes = await GetBytesAsync("npc/index.json", 256 * 1024, token);
            var catalog = RemoteNpcContract.ParseCatalog(Encoding.UTF8.GetString(bytes));
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath));
            AtomicWrite(_cachePath, Encoding.UTF8.GetString(bytes));
            return catalog;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch when (File.Exists(_cachePath)) { return RemoteNpcContract.ParseCatalog(File.ReadAllText(_cachePath, Encoding.UTF8)); }
    }

    public async Task<RemoteNpcManifest> FetchManifestAsync(RemoteNpcSummary summary, CancellationToken token)
    {
        byte[] bytes = await GetBytesAsync(summary.ManifestPath, 256 * 1024, token);
        if (!string.Equals(RemoteNpcContract.ComputeSha256(bytes), summary.ManifestSha256, StringComparison.Ordinal)) throw new InvalidDataException("NPC manifest hash mismatch.");
        string raw = Encoding.UTF8.GetString(bytes);
        RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(raw, summary);
        manifest.RawJson = raw;
        return manifest;
    }

    public async Task<Texture2D> LoadAvatarAsync(RemoteNpcSummary summary, CancellationToken token)
    {
        byte[] bytes = await GetBytesAsync(summary.AvatarPath, 256 * 1024, token);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = $"npc-avatar-{summary.NpcId}" };
        if (!ImageConversion.LoadImage(texture, bytes, true)) { UnityEngine.Object.Destroy(texture); throw new InvalidDataException("NPC avatar is not a supported image."); }
        if (texture.width > 2048 || texture.height > 2048) { UnityEngine.Object.Destroy(texture); throw new InvalidDataException("NPC avatar dimensions exceed the limit."); }
        _avatarAssets.Add(texture); return texture;
    }

    public void ReleaseAvatars() { foreach (UnityEngine.Object asset in _avatarAssets) if (asset != null) UnityEngine.Object.Destroy(asset); _avatarAssets.Clear(); }
    public void Dispose() => ReleaseAvatars();

    private async Task<byte[]> GetBytesAsync(string relativePath, int maxBytes, CancellationToken token)
    {
        Uri uri = new Uri(_root, relativePath);
        if (uri.Scheme != _root.Scheme || uri.Host != _root.Host || uri.Port != _root.Port || !uri.AbsoluteUri.StartsWith(_root.AbsoluteUri, StringComparison.Ordinal)) throw new InvalidDataException("NPC URL escapes the trusted root.");
        using var request = UnityWebRequest.Get(uri);
        request.redirectLimit = 0; request.timeout = Math.Max(1, (int)_timeout.TotalSeconds);
        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone) { token.ThrowIfCancellationRequested(); if (request.downloadedBytes > (ulong)maxBytes) { request.Abort(); throw new InvalidDataException("NPC response exceeds the size limit."); } await Task.Yield(); }
        if (request.result != UnityWebRequest.Result.Success) throw new IOException($"NPC content request failed ({request.responseCode}).");
        byte[] data = request.downloadHandler.data; if (data == null || data.Length == 0 || data.Length > maxBytes) throw new InvalidDataException("NPC response size is invalid."); return data;
    }

    public static void AtomicWrite(string path, string content)
    {
        string directory = Path.GetDirectoryName(path); Directory.CreateDirectory(directory);
        string temp = path + ".tmp"; string backup = path + ".bak";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        if (File.Exists(path)) { File.Replace(temp, path, backup); if (File.Exists(backup)) File.Delete(backup); }
        else File.Move(temp, path);
    }
}

public static class RemoteNpcContentRootResolver
{
    public static Uri Resolve()
    {
        foreach (var locator in Addressables.ResourceLocators)
        {
            if (!locator.Locate(AddressableHotUpdateReleaseLoader.ManifestAddress, typeof(TextAsset), out IList<IResourceLocation> locations)) continue;
            foreach (IResourceLocation location in locations)
            {
                if (!Uri.TryCreate(location.InternalId, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
                // NPC_CONTENT_BASE_URL and the published static NPC root share the
                // Addressables channel origin, not the release/bundle subdirectory.
                return new UriBuilder(uri.Scheme, uri.Host, uri.Port, "/").Uri;
            }
        }
        return new Uri("http://127.0.0.1:8081/");
    }
}

public sealed class InstalledNpcRecord
{
    public string NpcId { get; set; }
    public string ContentVersion { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    public string AvatarPath { get; set; }
    public string ManifestSha256 { get; set; }
    public string ManifestJson { get; set; }
}

public sealed class NpcContentInstaller
{
    private readonly IContentAssetProvider _provider;
    private readonly RemoteNpcCatalogClient _client;
    private readonly string _indexPath;
    private readonly Func<string, bool> _isSpawned;
    private readonly Dictionary<string, InstalledNpcRecord> _installed = new Dictionary<string, InstalledNpcRecord>(StringComparer.Ordinal);
    private readonly object _gate = new object();
    private CancellationTokenSource _active;
    private TaskCompletionSource<bool> _activeCompletion;
    public event Action Changed;
    public event Action<string, ContentDownloadProgress> ProgressChanged;
    public IReadOnlyCollection<InstalledNpcRecord> Installed => _installed.Values;
    public string ActiveNpcId { get; private set; }

    public NpcContentInstaller(
        IContentAssetProvider provider,
        RemoteNpcCatalogClient client,
        string storageRoot,
        Func<string, bool> isSpawned = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider)); _client = client ?? throw new ArgumentNullException(nameof(client));
        _indexPath = Path.Combine(storageRoot, "installed.json");
        _isSpawned = isSpawned;
        Load();
    }
    public bool TryGet(string npcId, out InstalledNpcRecord record) => _installed.TryGetValue(npcId, out record);
    public void Cancel() { lock (_gate) _active?.Cancel(); }

    public async Task CancelAndWaitAsync()
    {
        Task completion;
        lock (_gate)
        {
            _active?.Cancel();
            completion = _activeCompletion?.Task;
        }
        if (completion == null) return;
        try { await completion; }
        catch { }
    }

    public async Task<InstalledNpcRecord> InstallAsync(RemoteNpcSummary summary, CancellationToken token)
    {
        if (summary == null)
            throw new ArgumentNullException(nameof(summary));
        if (_isSpawned?.Invoke(summary.NpcId) == true)
            throw new InvalidOperationException("NPC_UPDATE_REQUIRES_RESTART");
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_active != null) throw new InvalidOperationException("NPC_INSTALL_BUSY");
            _active = linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            _activeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ActiveNpcId = summary.NpcId;
        }
        Changed?.Invoke();
        try
        {
            RemoteNpcManifest manifest = await _client.FetchManifestAsync(summary, linked.Token);
            string expectedBuildId = "windows-x64-" + Application.version;
            if (!string.Equals(manifest.PlayerBuildId, expectedBuildId, StringComparison.Ordinal))
                throw new InvalidDataException("NPC content targets a different Player build.");
            if (!IsCompatiblePlayerVersion(summary, Application.version))
                throw new InvalidDataException("NPC content is not compatible with this Player version.");
            var progress = new Progress<ContentDownloadProgress>(p => ProgressChanged?.Invoke(summary.NpcId, p));
            await _provider.DownloadDependenciesAsync(manifest.DownloadAddresses, progress, linked.Token);
            if (await _provider.GetDownloadSizeAsync(manifest.DownloadAddresses, linked.Token) != 0) throw new IOException("NPC dependency cache is incomplete after download.");
            await ValidateDownloadedAssetsAsync(manifest, linked.Token);
            var record = new InstalledNpcRecord { NpcId = summary.NpcId, ContentVersion = summary.ContentVersion, DisplayName = summary.DisplayName, Description = summary.Description, AvatarPath = summary.AvatarPath, ManifestSha256 = summary.ManifestSha256, ManifestJson = manifest.RawJson };
            bool hadPrevious = _installed.TryGetValue(summary.NpcId, out InstalledNpcRecord previous);
            _installed[summary.NpcId] = record;
            try { Save(); }
            catch
            {
                if (hadPrevious) _installed[summary.NpcId] = previous;
                else _installed.Remove(summary.NpcId);
                throw;
            }
            return record;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, linked))
                {
                    _active.Dispose();
                    _active = null;
                    ActiveNpcId = null;
                    _activeCompletion?.TrySetResult(true);
                    _activeCompletion = null;
                }
            }
            Changed?.Invoke();
        }
    }

    public async Task<bool> IsCacheMissingAsync(string npcId, CancellationToken token)
    {
        if (!TryGet(npcId, out InstalledNpcRecord record)) return false;
        RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(record.ManifestJson);
        return await _provider.GetDownloadSizeAsync(manifest.DownloadAddresses, token) > 0;
    }

    private void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath));
        string temp = _indexPath + ".tmp"; if (File.Exists(temp)) File.Delete(temp);
        if (!File.Exists(_indexPath)) return;
        try
        {
            JArray array = JArray.Parse(File.ReadAllText(_indexPath, Encoding.UTF8));
            foreach (JObject item in array.OfType<JObject>())
            {
                var record = item.ToObject<InstalledNpcRecord>();
                RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(record.ManifestJson);
                string actualHash = RemoteNpcContract.ComputeSha256(Encoding.UTF8.GetBytes(record.ManifestJson ?? string.Empty));
                if (manifest.NpcId == record.NpcId && manifest.ContentVersion == record.ContentVersion &&
                    string.Equals(actualHash, record.ManifestSha256, StringComparison.Ordinal))
                    _installed[record.NpcId] = record;
            }
        }
        catch (Exception ex) { Debug.LogWarning($"[NPC Content] Installed index was ignored: {ex.GetBaseException().Message}"); _installed.Clear(); }
    }
    private void Save() => RemoteNpcCatalogClient.AtomicWrite(_indexPath, JArray.FromObject(_installed.Values.OrderBy(x => x.NpcId)).ToString(Formatting.None));

    private async Task ValidateDownloadedAssetsAsync(
        RemoteNpcManifest manifest,
        CancellationToken token)
    {
        if (!manifest.UsesBuiltinAnimationDriver)
        {
            using ContentAssetLease<TextAsset> dll = await _provider.LoadAssetAsync<TextAsset>(
                manifest.AnimationScriptAddress,
                token);
            byte[] bytes = dll.Asset?.bytes;
            if (bytes == null || bytes.LongLength != manifest.AnimationLength ||
                !string.Equals(
                    RemoteNpcContract.ComputeSha256(bytes),
                    manifest.AnimationSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("NPC animation assembly integrity check failed.");
        }

        using ContentAssetLease<GameObject> visual = await _provider.LoadAssetAsync<GameObject>(
            manifest.PrefabAddress,
            token);
        CharacterVisualController.ValidateVisualInstance(visual.Asset, manifest.SchemaVersion >= 2);
        if (manifest.SchemaVersion == 1)
        {
            if (visual.Asset.GetComponentInChildren<Animator>(true) == null)
                throw new InvalidDataException("NPC visual does not contain an Animator.");
            using ContentAssetLease<RuntimeAnimatorController> controller =
                await _provider.LoadAssetAsync<RuntimeAnimatorController>(
                    manifest.AnimatorControllerAddress,
                    token);
            if (controller.Asset == null)
                throw new InvalidDataException("NPC animator controller resolved to null.");
        }
    }

    public static bool IsCompatiblePlayerVersion(RemoteNpcSummary summary, string playerVersion)
    {
        try
        {
            var current = new Version(playerVersion);
            return current.CompareTo(new Version(summary.MinPlayerVersion)) >= 0 &&
                   current.CompareTo(new Version(summary.MaxPlayerVersion)) <= 0;
        }
        catch
        {
            return false;
        }
    }
}

