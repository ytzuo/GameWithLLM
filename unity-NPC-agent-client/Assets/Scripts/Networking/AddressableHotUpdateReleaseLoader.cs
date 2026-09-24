using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public interface IHotUpdateReleaseLoader
{
    Task<HotUpdateReleaseManifest> LoadManifestAsync(CancellationToken cancellationToken);
    void ValidateCompatibility(HotUpdateReleaseManifest manifest);
    IReadOnlyList<string> GetRequiredAddresses(HotUpdateReleaseManifest manifest);
    Task<LoadedToolSetRelease> LoadCandidateAsync(
        HotUpdateReleaseManifest manifest,
        CancellationToken cancellationToken);
}

[Serializable]
public class HotUpdateArtifactDeclaration
{
    public string address;
    public string fileName;
    public long length;
    public string sha256;
}

[Serializable]
public sealed class HotUpdateCatalogDeclaration : HotUpdateArtifactDeclaration
{
    public string catalogId;
}

[Serializable]
public sealed class HotUpdateToolPackageDeclaration
{
    public string packageId;
    public string packageVersion;
    public string assemblyName;
    public HotUpdateArtifactDeclaration assembly;
    public HotUpdateArtifactDeclaration debugSymbols;
}

[Serializable]
public sealed class HotUpdateReleaseManifest
{
    public int schemaVersion;
    public string releaseId;
    public string contentVersion;
    public string toolSetVersion;
    public string catalogVersion;
    public string playerBuildId;
    public string minPlayerVersion;
    public string maxPlayerVersion;
    public HotUpdateCatalogDeclaration[] catalogs;
    public HotUpdateArtifactDeclaration[] aotMetadata;
    public HotUpdateToolPackageDeclaration[] toolPackages;
    public ToolReleaseDeclaration[] activeTools;
    public RetiredToolDeclaration[] retiredTools;
    public ToolHistoryDeclaration[] toolHistory;
}

// A2 的候选加载器。所有 Addressable bytes 都先复制并完成长度/hash/JSON 校验，
// 之后才调用 HybridCLR 或 Assembly.Load，避免损坏候选产生半套运行时状态。
public sealed class AddressableHotUpdateReleaseLoader : IHotUpdateReleaseLoader
{
    public const string ManifestAddress = "hotfix/release/manifest";
    private const string ToolMetadataId = "tool_metadata.zh-CN";
    private const string AgentMessagesId = "agent_messages.zh-CN";
    private const string UiId = "ui.zh-CN";
    private readonly IContentAssetProvider _provider;
    private readonly HybridClrToolPackageLoader _toolPackageLoader;

    public AddressableHotUpdateReleaseLoader(
        IContentAssetProvider provider,
        HybridClrToolPackageLoader toolPackageLoader = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _toolPackageLoader = toolPackageLoader ?? new HybridClrToolPackageLoader(
            new AddressableHotUpdateArtifactSource(provider));
    }

    public async Task<HotUpdateReleaseManifest> LoadManifestAsync(
        CancellationToken cancellationToken)
    {
        using ContentAssetLease<TextAsset> lease =
            await _provider.LoadAssetAsync<TextAsset>(ManifestAddress, cancellationToken);
        string json = lease.Asset != null
            ? lease.Asset.text
            : throw new InvalidDataException("The A2 release manifest TextAsset is null.");
        HotUpdateReleaseManifest manifest;
        try
        {
            JToken root = JToken.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Ignore,
                LineInfoHandling = LineInfoHandling.Ignore
            });
            manifest = root.ToObject<HotUpdateReleaseManifest>(JsonSerializer.Create(
                new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error }));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The A2 release manifest is invalid JSON.", ex);
        }
        ValidateManifest(manifest);
        return manifest;
    }

    public void ValidateCompatibility(HotUpdateReleaseManifest manifest)
    {
        ValidateManifest(manifest);
        string expectedBuildId = "windows-x64-" + Application.version;
        if (!string.Equals(manifest.playerBuildId, expectedBuildId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Release '{manifest.releaseId}' targets Player build '{manifest.playerBuildId}', " +
                $"not '{expectedBuildId}'.");
        if (CompareVersions(Application.version, manifest.minPlayerVersion) < 0 ||
            CompareVersions(Application.version, manifest.maxPlayerVersion) > 0)
        {
            throw new InvalidDataException(
                $"Release '{manifest.releaseId}' is not compatible with Player '{Application.version}'.");
        }
#if !UNITY_EDITOR && !UNITY_STANDALONE_WIN
        throw new PlatformNotSupportedException("A2 hot-update releases support Windows players only.");
#endif
    }

    public IReadOnlyList<string> GetRequiredAddresses(HotUpdateReleaseManifest manifest)
    {
        ValidateManifest(manifest);
        return manifest.catalogs.Cast<HotUpdateArtifactDeclaration>()
            .Concat(_toolPackageLoader.GetRequiredArtifacts(manifest))
            .Select(item => item.address)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<LoadedToolSetRelease> LoadCandidateAsync(
        HotUpdateReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        ValidateCompatibility(manifest);
        var bytesByAddress = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (HotUpdateArtifactDeclaration artifact in manifest.catalogs)
        {
            using ContentAssetLease<TextAsset> lease =
                await _provider.LoadAssetAsync<TextAsset>(artifact.address, cancellationToken);
            byte[] bytes = lease.Asset != null
                ? lease.Asset.bytes.ToArray()
                : throw new InvalidDataException($"Address '{artifact.address}' returned a null TextAsset.");
            ValidateArtifact(artifact, bytes);
            bytesByAddress.Add(artifact.address, bytes);
        }

        string toolMetadata = ReadCatalog(manifest, bytesByAddress, ToolMetadataId);
        string agentMessages = ReadCatalog(manifest, bytesByAddress, AgentMessagesId);
        string ui = ReadCatalog(manifest, bytesByAddress, UiId);
        ValidateCatalogEnvelope(toolMetadata, manifest.contentVersion, "tools");
        ClientTextCatalog.Parse(agentMessages, manifest.contentVersion);
        ClientTextCatalog.Parse(ui, manifest.contentVersion);

        IReadOnlyList<LoadedToolPack> packs = await _toolPackageLoader.LoadAsync(
            manifest,
            cancellationToken);

        return new LoadedToolSetRelease(
            manifest.releaseId,
            manifest.toolSetVersion,
            manifest.catalogVersion,
            manifest.activeTools,
            manifest.retiredTools ?? Array.Empty<RetiredToolDeclaration>(),
            manifest.toolHistory ?? Array.Empty<ToolHistoryDeclaration>(),
            packs,
            toolMetadata,
            agentMessages,
            ui);
    }

    private static IEnumerable<HotUpdateArtifactDeclaration> EnumerateArtifacts(
        HotUpdateReleaseManifest manifest)
    {
        foreach (HotUpdateCatalogDeclaration catalog in manifest.catalogs)
            yield return catalog;
        foreach (HotUpdateArtifactDeclaration metadata in manifest.aotMetadata)
            yield return metadata;
        foreach (HotUpdateToolPackageDeclaration package in manifest.toolPackages)
        {
            yield return package.assembly;
            if (package.debugSymbols != null && !string.IsNullOrWhiteSpace(package.debugSymbols.address))
                yield return package.debugSymbols;
        }
    }

    private static void ValidateManifest(HotUpdateReleaseManifest manifest)
    {
        if (manifest == null || manifest.schemaVersion != 1 ||
            string.IsNullOrWhiteSpace(manifest.releaseId) ||
            string.IsNullOrWhiteSpace(manifest.contentVersion) ||
            string.IsNullOrWhiteSpace(manifest.toolSetVersion) ||
            string.IsNullOrWhiteSpace(manifest.catalogVersion) ||
            !string.Equals(manifest.contentVersion, manifest.catalogVersion, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.playerBuildId) ||
            string.IsNullOrWhiteSpace(manifest.minPlayerVersion) ||
            string.IsNullOrWhiteSpace(manifest.maxPlayerVersion))
            throw new InvalidDataException("A2 release manifest header is incomplete or inconsistent.");
        manifest.catalogs ??= Array.Empty<HotUpdateCatalogDeclaration>();
        manifest.aotMetadata ??= Array.Empty<HotUpdateArtifactDeclaration>();
        manifest.toolPackages ??= Array.Empty<HotUpdateToolPackageDeclaration>();
        manifest.activeTools ??= Array.Empty<ToolReleaseDeclaration>();
        if (manifest.catalogs.Length != 3 || manifest.aotMetadata.Length == 0)
            throw new InvalidDataException("A2 release must contain three catalogs and AOT metadata.");
        var catalogIds = new HashSet<string>(manifest.catalogs.Select(item => item.catalogId), StringComparer.Ordinal);
        if (!catalogIds.SetEquals(new[] { ToolMetadataId, AgentMessagesId, UiId }))
            throw new InvalidDataException("A2 release catalog set is incomplete.");
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (HotUpdateArtifactDeclaration artifact in EnumerateArtifacts(manifest))
        {
            if (artifact == null || string.IsNullOrWhiteSpace(artifact.address) ||
                string.IsNullOrWhiteSpace(artifact.fileName) || artifact.length <= 0 ||
                artifact.sha256 == null || artifact.sha256.Length != 64 ||
                artifact.sha256.Any(character => !Uri.IsHexDigit(character)) ||
                !addresses.Add(artifact.address))
                throw new InvalidDataException("A2 release contains an invalid or duplicate artifact.");
            if (Path.GetFileName(artifact.fileName) != artifact.fileName)
                throw new InvalidDataException($"Invalid artifact file name '{artifact.fileName}'.");
        }
        foreach (HotUpdateToolPackageDeclaration package in manifest.toolPackages)
        {
            if (package == null || string.IsNullOrWhiteSpace(package.packageId) ||
                string.IsNullOrWhiteSpace(package.packageVersion) ||
                string.IsNullOrWhiteSpace(package.assemblyName) || package.assembly == null)
                throw new InvalidDataException("A2 tool package declaration is incomplete.");
        }
        var selectedPackages = new HashSet<string>(manifest.activeTools
            .Where(tool => string.Equals(tool.source, "hot-update", StringComparison.Ordinal))
            .Select(tool => tool.packageId + "\n" + tool.packageVersion), StringComparer.Ordinal);
        var declaredPackages = new HashSet<string>(StringComparer.Ordinal);
        foreach (HotUpdateToolPackageDeclaration package in manifest.toolPackages)
        {
            string key = package.packageId + "\n" + package.packageVersion;
            if (!declaredPackages.Add(key))
                throw new InvalidDataException($"Duplicate A2 tool package '{package.packageId}'.");
            ToolReleaseDeclaration[] packageTools = manifest.activeTools.Where(tool =>
                string.Equals(tool.source, "hot-update", StringComparison.Ordinal) &&
                string.Equals(tool.packageId, package.packageId, StringComparison.Ordinal) &&
                string.Equals(tool.packageVersion, package.packageVersion, StringComparison.Ordinal)).ToArray();
            if (packageTools.Length == 0 || packageTools.Any(tool =>
                    !string.Equals(tool.assemblyName, package.assemblyName, StringComparison.Ordinal) ||
                    !string.Equals(tool.assemblyHash, package.assembly.sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    $"A2 package '{package.packageId}' does not match its active tool declarations.");
        }
        if (!selectedPackages.SetEquals(declaredPackages))
            throw new InvalidDataException("A2 release package set does not match the selected ToolSet.");
    }

    private static void ValidateArtifact(HotUpdateArtifactDeclaration artifact, byte[] bytes)
    {
        if (bytes.LongLength != artifact.length)
            throw new InvalidDataException(
                $"Artifact '{artifact.address}' length does not match its release manifest.");
        using SHA256 sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        if (!string.Equals(hash, artifact.sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Artifact '{artifact.address}' SHA-256 does not match its release manifest.");
    }

    private static string ReadCatalog(
        HotUpdateReleaseManifest manifest,
        IReadOnlyDictionary<string, byte[]> bytes,
        string id)
    {
        HotUpdateCatalogDeclaration catalog = manifest.catalogs.Single(item => item.catalogId == id);
        return new System.Text.UTF8Encoding(false, true).GetString(bytes[catalog.address]);
    }

    private static void ValidateCatalogEnvelope(string json, string version, string payloadProperty)
    {
        JObject root;
        try { root = JObject.Parse(json); }
        catch (JsonException ex) { throw new InvalidDataException("Tool metadata JSON is invalid.", ex); }
        if ((int?)root["schemaVersion"] != 1 ||
            (string)root["contentVersion"] != version ||
            (string)root["locale"] != "zh-CN" || root[payloadProperty]?.Type != JTokenType.Object)
            throw new InvalidDataException("Tool metadata catalog envelope is invalid.");
    }

    private static int CompareVersions(string left, string right)
    {
        if (!Version.TryParse(left, out Version a) || !Version.TryParse(right, out Version b))
            throw new InvalidDataException("Player compatibility versions must be numeric dotted versions.");
        return a.CompareTo(b);
    }
}
