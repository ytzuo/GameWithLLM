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
    public IReadOnlyDictionary<string, string> Texts { get; internal set; }
    public IReadOnlyList<RemoteNpcSummary> Npcs { get; internal set; }
}

public sealed class RemoteNpcSummary
{
    public string NpcId { get; internal set; }
    public string DisplayName { get; internal set; }
    public string Description { get; internal set; }
    public string AvatarPath { get; internal set; }
    public string ManifestPath { get; internal set; }

    public static RemoteNpcSummary FromInstalled(InstalledNpcRecord record)
    {
        if (record == null)
            throw new ArgumentNullException(nameof(record));
        return new RemoteNpcSummary
        {
            NpcId = record.NpcId,
            DisplayName = record.DisplayName,
            Description = record.Description,
            AvatarPath = record.AvatarPath,
            ManifestPath = $"npc/{record.NpcId}/npc.json",
        };
    }
}

public sealed class RemoteNpcManifest
{
    public string NpcId { get; internal set; }
    public string PrefabAddress { get; internal set; }
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

    public IReadOnlyList<string> DownloadAddresses => new[] { PrefabAddress }
            .Concat(UsesBuiltinAnimationDriver ? Array.Empty<string>() : new[] { AnimationScriptAddress })
            .Distinct(StringComparer.Ordinal).ToArray();
}

public static class RemoteNpcContract
{
    private static readonly Regex StableId = new Regex(@"^[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new Regex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly string[] TextKeys =
    {
        "remoteTab", "localTab", "download", "spawn", "cancel", "retry", "close", "refresh",
        "alreadySpawned", "restartRequired", "progressFormat", "downloading", "installed",
        "remoteEmpty", "localEmpty", "errorFetch", "errorContent", "errorResource",
        "errorDownload", "errorCancelled", "errorSpawn", "errorBusy"
    };

    public static RemoteNpcCatalog ParseCatalog(string json)
    {
        JObject root = ParseObject(json, 256 * 1024);
        Exact(root, "texts", "npcs");
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
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RemoteNpcSummary>(npcs.Count);
        foreach (JToken token in npcs)
        {
            if (!(token is JObject item)) throw new InvalidDataException("NPC catalog item must be an object.");
            Exact(item, "name", "displayName", "description", "avatarPath", "manifestPath");
            string npcId = ValidateName(String(item, "name", 1, 64));
            if (!ids.Add(npcId)) throw new InvalidDataException($"Duplicate NPC '{npcId}'.");
            string expectedPrefix = $"npc/{npcId}/";
            string avatarPath = TrustedPath(String(item, "avatarPath", 1, 256), expectedPrefix, "avatar.png");
            string manifestPath = TrustedPath(String(item, "manifestPath", 1, 256), expectedPrefix, "npc.json");
            result.Add(new RemoteNpcSummary
            {
                NpcId = npcId,
                DisplayName = PlainText(item, "displayName", 1, 80),
                Description = PlainText(item, "description", 1, 600),
                AvatarPath = avatarPath, ManifestPath = manifestPath
            });
        }
        return new RemoteNpcCatalog { Texts = textMap, Npcs = result };
    }

    public static RemoteNpcManifest ParseManifest(string json, RemoteNpcSummary expected = null)
    {
        JObject root = ParseObject(json, 256 * 1024);
        Exact(root, "name", "visual", "animationDriver", "profile", "systemPrompt");
        string npcId = ValidateName(String(root, "name", 1, 64));
        if (expected != null && npcId != expected.NpcId)
            throw new InvalidDataException("NPC manifest identity does not match its catalog entry.");
        JObject visual = Object(root, "visual");
        Exact(visual, "prefabAddress");
        string prefix = $"npc/{npcId}/";
        JObject profile = Object(root, "profile");
        Exact(profile, "npcId", "displayName", "personality", "speakingStyle", "identity", "responsibilities", "worldKnowledge", "forbiddenTopics");
        if (String(profile, "npcId", 1, 64) != npcId) throw new InvalidDataException("Profile npcId mismatch.");
        PlainText(profile, "displayName", 1, 80); PlainText(profile, "speakingStyle", 1, 2000); PlainText(profile, "identity", 1, 300);
        ValidatePlainArray(profile, "personality", 1, 64, 1000);
        ValidatePlainArray(profile, "responsibilities", 1, 64, 1000);
        ValidatePlainArray(profile, "worldKnowledge", 1, 64, 1000);
        ValidatePlainArray(profile, "forbiddenTopics", 1, 64, 1000);
        JObject prompt = Object(root, "systemPrompt");
        Exact(prompt, "locale", "template");
        if (String(prompt, "locale", 1, 32) != "zh-CN") throw new InvalidDataException("System prompt locale is invalid.");
        string template = String(prompt, "template", 1, 64 * 1024);
        string[] placeholders = { "displayName", "npcId", "identity", "personality", "speakingStyle", "responsibilities", "worldKnowledge", "forbiddenTopics" };
        foreach (string key in placeholders)
            if (!template.Contains("{{" + key + "}}", StringComparison.Ordinal)) throw new InvalidDataException("System prompt placeholder missing.");
        foreach (Match match in Regex.Matches(template, @"\{\{([^{}]+)\}\}"))
            if (!placeholders.Contains(match.Groups[1].Value)) throw new InvalidDataException("Unknown system prompt placeholder.");
        var result = new RemoteNpcManifest { NpcId = npcId, PrefabAddress = Address(visual, "prefabAddress", prefix), RawJson = root.ToString(Formatting.None) };
        if (result.PrefabAddress != prefix + "visual") throw new InvalidDataException("Invalid visual address.");
        JObject driver = Object(root, "animationDriver");
        string kind = String(driver, "kind", 1, 32);
        if (kind == "builtin")
        {
            Exact(driver, "kind", "driverId");
            string driverId = String(driver, "driverId", 1, 64);
            if (driverId != StandardLocomotionAnimationDriver.DriverId) throw new InvalidDataException("Unknown builtin NPC animation driver.");
            result.AnimationDriverKind = kind; result.BuiltinAnimationDriverId = driverId;
        }
        else if (kind == "hotUpdate")
        {
            Exact(driver, "kind", "address", "assemblyName", "entryType", "length", "sha256");
            ReadHotUpdateDriver(driver, prefix, result);
            if (result.AnimationScriptAddress != prefix + "animation-script") throw new InvalidDataException("Invalid animation address.");
        }
        else throw new InvalidDataException("Unknown NPC animation driver kind.");
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

    public static string ValidateName(string name)
    {
        Stable(name, StableId, "name");
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new InvalidDataException("NPC name is a reserved Windows device name.");
        return name;
    }

    public static string ComputeSha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(x => x.ToString("x2")));
    }

    private static JObject ParseObject(string json, int maxChars)
    {
        if (string.IsNullOrEmpty(json) || json.Length > maxChars || Encoding.UTF8.GetByteCount(json) > maxChars) throw new InvalidDataException("JSON size is invalid.");
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        try
        {
            JObject result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (reader.Read()) throw new InvalidDataException("JSON contains trailing data.");
            return result;
        }
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
    private static string Stable(string value, Regex regex, string name) => regex.IsMatch(value) ? value : throw new InvalidDataException($"'{name}' is invalid.");
    private static string TrustedPath(string p, string prefix, string suffix) { if (!string.Equals(p, prefix + suffix, StringComparison.Ordinal) || p.Contains("%") || p.Contains("?") || p.Contains("#") || p.Contains("\\")) throw new InvalidDataException("Content path escapes the trusted root."); return p; }
    private static string Address(JObject o, string n, string prefix) { string v = String(o, n, 1, 256); if (!v.StartsWith(prefix, StringComparison.Ordinal) || v.Length == prefix.Length || v.Contains("..") || v.Contains(":") || v.Contains("\\") || v.Contains("%") || v.Contains("?") || v.Contains("#") || v.Contains("//")) throw new InvalidDataException($"'{n}' is outside the NPC namespace."); return v; }
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
        _cachePath = Path.Combine(storageRoot, "catalog-cache-current.json"); _timeout = timeout;
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
        catch (IOException) when (File.Exists(_cachePath))
        { return RemoteNpcContract.ParseCatalog(File.ReadAllText(_cachePath, Encoding.UTF8)); }
    }

    public async Task<RemoteNpcManifest> FetchManifestAsync(RemoteNpcSummary summary, CancellationToken token)
    {
        byte[] bytes = await GetBytesAsync(summary.ManifestPath, 256 * 1024, token);
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
        token.ThrowIfCancellationRequested();
        Uri uri = new Uri(_root, relativePath);
        if (uri.Scheme != _root.Scheme || uri.Host != _root.Host || uri.Port != _root.Port || !uri.AbsoluteUri.StartsWith(_root.AbsoluteUri, StringComparison.Ordinal)) throw new InvalidDataException("NPC URL escapes the trusted root.");
        using var request = UnityWebRequest.Get(uri);
        request.redirectLimit = 0; request.timeout = Math.Max(1, (int)_timeout.TotalSeconds);
        UnityWebRequestAsyncOperation operation = request.SendWebRequest();
        while (!operation.isDone) { token.ThrowIfCancellationRequested(); if (request.downloadedBytes > (ulong)maxBytes) { request.Abort(); throw new InvalidDataException("NPC response exceeds the size limit."); } await Task.Yield(); }
        token.ThrowIfCancellationRequested();
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
    [JsonProperty("name")] public string NpcId { get; set; }
    [JsonProperty("displayName")] public string DisplayName { get; set; }
    [JsonProperty("description")] public string Description { get; set; }
    [JsonProperty("avatarPath")] public string AvatarPath { get; set; }
    [JsonProperty("manifestJson")] public string ManifestJson { get; set; }
}

public sealed class NpcContentInstaller
{
    private readonly IContentAssetProvider _provider;
    private readonly RemoteNpcCatalogClient _client;
    private readonly string _indexPath;
    private readonly Func<string, bool> _isSpawned;
    private readonly Dictionary<string, InstalledNpcRecord> _installed = new Dictionary<string, InstalledNpcRecord>(StringComparer.OrdinalIgnoreCase);
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
        _indexPath = Path.Combine(storageRoot, "installed-current.json");
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
            throw new InvalidOperationException("NPC_ALREADY_SPAWNED");
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_active != null) throw new InvalidOperationException("NPC_INSTALL_BUSY");
            _active = linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            _activeCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ActiveNpcId = summary.NpcId;
        }
        try
        {
            Changed?.Invoke();
            linked.Token.ThrowIfCancellationRequested();
            RemoteNpcManifest manifest = await _client.FetchManifestAsync(summary, linked.Token);
            await NpcPresentationPreparation.RequireAddressesAsync(_provider, manifest, linked.Token);
            var progress = new Progress<ContentDownloadProgress>(p => ProgressChanged?.Invoke(summary.NpcId, p));
            await _provider.DownloadDependenciesAsync(manifest.DownloadAddresses, progress, linked.Token);
            if (await _provider.GetDownloadSizeAsync(manifest.DownloadAddresses, linked.Token) != 0) throw new IOException("NPC dependency cache is incomplete after download.");
            await NpcPresentationPreparation.ValidateAsync(_provider, manifest, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_isSpawned?.Invoke(summary.NpcId) == true)
                throw new InvalidOperationException("NPC_ALREADY_SPAWNED");
            var record = new InstalledNpcRecord { NpcId = summary.NpcId, DisplayName = summary.DisplayName, Description = summary.Description, AvatarPath = summary.AvatarPath, ManifestJson = manifest.RawJson };
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
        await NpcPresentationPreparation.RequireAddressesAsync(_provider, manifest, token);
        if (await _provider.GetDownloadSizeAsync(manifest.DownloadAddresses, token) > 0) return true;
        try
        {
            await NpcPresentationPreparation.ValidateAsync(_provider, manifest, token);
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message == "NPC_RESOURCE_RESTART_REQUIRED") { throw; }
        catch { return true; }
    }

    public async Task ValidateInstalledAsync(string npcId, CancellationToken token)
    {
        if (!TryGet(npcId, out InstalledNpcRecord record))
            throw new InvalidOperationException($"NPC_CONTENT_NOT_INSTALLED:{npcId}");
        await NpcPresentationPreparation.ValidateAsync(_provider,
            RemoteNpcContract.ParseManifest(record.ManifestJson), token);
    }

    public bool RequiresReinstall { get; private set; }

    private void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath));
        string temp = _indexPath + ".tmp"; if (File.Exists(temp)) File.Delete(temp);
        if (!File.Exists(_indexPath))
        {
            RequiresReinstall = File.Exists(Path.Combine(Path.GetDirectoryName(_indexPath), "installed.json"));
            return;
        }
        try
        {
            JArray array = JArray.Parse(File.ReadAllText(_indexPath, Encoding.UTF8), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JToken token in array)
            {
                if (!(token is JObject item) || !item.Properties().Select(x => x.Name).OrderBy(x => x).SequenceEqual(new[] { "name", "displayName", "description", "avatarPath", "manifestJson" }.OrderBy(x => x))) throw new InvalidDataException("Installed record fields are invalid.");
                var record = item.ToObject<InstalledNpcRecord>();
                if (string.IsNullOrWhiteSpace(record.DisplayName) || string.IsNullOrWhiteSpace(record.Description) || record.AvatarPath != $"npc/{record.NpcId}/avatar.png") throw new InvalidDataException("Installed record metadata is invalid.");
                RemoteNpcManifest manifest = RemoteNpcContract.ParseManifest(record.ManifestJson);
                if (manifest.NpcId != record.NpcId || !names.Add(record.NpcId)) throw new InvalidDataException("Installed NPC identity invalid.");
                _installed[record.NpcId] = record;
            }
        }
        catch (Exception) { Debug.LogWarning("[NPC Content] Installed index is invalid; reinstall required."); _installed.Clear(); RequiresReinstall = true; }
    }
    private void Save() => RemoteNpcCatalogClient.AtomicWrite(_indexPath, JArray.FromObject(_installed.Values.OrderBy(x => x.NpcId)).ToString(Formatting.None));

}
