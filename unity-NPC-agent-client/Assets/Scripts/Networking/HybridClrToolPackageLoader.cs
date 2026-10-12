using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HybridCLR;
using UnityEngine;

public static class HotUpdateLoadErrorCodes
{
    public const string ArtifactLoadFailed = "HOT_UPDATE_ARTIFACT_LOAD_FAILED";
    public const string ArtifactLengthMismatch = "HOT_UPDATE_ARTIFACT_LENGTH_MISMATCH";
    public const string ArtifactHashMismatch = "HOT_UPDATE_ARTIFACT_HASH_MISMATCH";
    public const string MetadataLoadFailed = "HOT_UPDATE_METADATA_LOAD_FAILED";
    public const string PackageIdentityMismatch = "HOT_UPDATE_PACKAGE_IDENTITY_MISMATCH";
    public const string AssemblyLoadFailed = "HOT_UPDATE_ASSEMBLY_LOAD_FAILED";
    public const string ToolDiscoveryFailed = "HOT_UPDATE_TOOL_DISCOVERY_FAILED";
}

public sealed class HotUpdateLoadException : Exception
{
    public HotUpdateLoadException(
        string errorCode,
        string stage,
        string message,
        Exception innerException = null,
        string packageId = null,
        string packageVersion = null,
        string assemblyHash = null) : base(message, innerException)
    {
        ErrorCode = errorCode;
        Stage = stage;
        PackageId = packageId;
        PackageVersion = packageVersion;
        AssemblyHash = assemblyHash;
    }

    public string ErrorCode { get; }
    public string Stage { get; }
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string AssemblyHash { get; }
}

public interface IHotUpdateArtifactSource
{
    Task<byte[]> LoadBytesAsync(
        HotUpdateArtifactDeclaration artifact,
        CancellationToken cancellationToken);
}

public sealed class AddressableHotUpdateArtifactSource : IHotUpdateArtifactSource
{
    private readonly IContentAssetProvider _provider;

    public AddressableHotUpdateArtifactSource(IContentAssetProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<byte[]> LoadBytesAsync(
        HotUpdateArtifactDeclaration artifact,
        CancellationToken cancellationToken)
    {
        try
        {
            using ContentAssetLease<TextAsset> lease =
                await _provider.LoadAssetAsync<TextAsset>(artifact.address, cancellationToken);
            return lease.Asset != null
                ? lease.Asset.bytes.ToArray()
                : throw new InvalidDataException($"Address '{artifact.address}' returned a null TextAsset.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HotUpdateLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.ArtifactLoadFailed,
                "artifact-load",
                $"Could not load hot-update artifact '{artifact.address}'.",
                ex);
        }
    }
}

// Addressables owns delivery of HybridCLR metadata and tool assemblies. All selected
// artifacts are copied and verified before HybridCLR/Assembly.Load creates side effects.
public sealed class HybridClrToolPackageLoader
{
    private static readonly ConcurrentDictionary<string, Lazy<LoadedToolPack>> LoadedPackages =
        new ConcurrentDictionary<string, Lazy<LoadedToolPack>>(StringComparer.Ordinal);
    private readonly IHotUpdateArtifactSource _source;
    private readonly bool _loadDebugSymbols;

    public HybridClrToolPackageLoader(
        IHotUpdateArtifactSource source,
        bool? loadDebugSymbols = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _loadDebugSymbols = loadDebugSymbols ?? UnityEngine.Debug.isDebugBuild;
    }

    public IReadOnlyList<HotUpdateArtifactDeclaration> GetRequiredArtifacts(
        HotUpdateReleaseManifest manifest)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        var selected = SelectedPackageKeys(manifest);
        var artifacts = new List<HotUpdateArtifactDeclaration>(manifest.aotMetadata);
        foreach (HotUpdateToolPackageDeclaration package in manifest.toolPackages)
        {
            if (!selected.Contains(PackageKey(package)))
                continue;
            artifacts.Add(package.assembly);
            if (_loadDebugSymbols && package.debugSymbols != null &&
                !string.IsNullOrWhiteSpace(package.debugSymbols.address))
                artifacts.Add(package.debugSymbols);
        }
        return artifacts;
    }

    public async Task<IReadOnlyList<LoadedToolPack>> LoadAsync(
        HotUpdateReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        try
        {
            return await LoadCoreAsync(manifest, cancellationToken);
        }
        catch (HotUpdateLoadException ex)
        {
            UnityEngine.Debug.LogError(
                $"[Hot Update] releaseId='{manifest?.releaseId ?? "unknown"}' " +
                $"packageId='{ex.PackageId ?? "-"}' version='{ex.PackageVersion ?? "-"}' " +
                $"hash='{ex.AssemblyHash ?? "-"}' stage='{ex.Stage}' code='{ex.ErrorCode}' " +
                $"elapsedMs={timer.ElapsedMilliseconds}.");
            throw;
        }
    }

    private async Task<IReadOnlyList<LoadedToolPack>> LoadCoreAsync(
        HotUpdateReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        Stopwatch total = Stopwatch.StartNew();
        var bytesByAddress = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (HotUpdateArtifactDeclaration artifact in GetRequiredArtifacts(manifest))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await _source.LoadBytesAsync(artifact, cancellationToken);
            ValidateArtifact(artifact, bytes);
            bytesByAddress.Add(artifact.address, bytes);
        }

        Stopwatch metadataTimer = Stopwatch.StartNew();
        foreach (HotUpdateArtifactDeclaration metadata in manifest.aotMetadata)
            LoadMetadata(manifest.releaseId, metadata, bytesByAddress[metadata.address]);
        LogStage(manifest.releaseId, null, null, null, "metadata", metadataTimer.ElapsedMilliseconds);

        var loaded = new List<LoadedToolPack>();
        HashSet<string> selected = SelectedPackageKeys(manifest);
        foreach (HotUpdateToolPackageDeclaration package in manifest.toolPackages)
        {
            if (!selected.Contains(PackageKey(package)))
                continue;
            ValidatePackageIdentity(package);
            Stopwatch packageTimer = Stopwatch.StartNew();
            string cacheKey = PackageKey(package) + "\n" + package.assembly.sha256.ToLowerInvariant();
            Lazy<LoadedToolPack> lazy = LoadedPackages.GetOrAdd(
                cacheKey,
                _ => new Lazy<LoadedToolPack>(
                    () => LoadPackage(package, bytesByAddress),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            LoadedToolPack pack;
            try
            {
                pack = lazy.Value;
            }
            catch (HotUpdateLoadException ex)
            {
                LoadedPackages.TryRemove(cacheKey, out _);
                throw new HotUpdateLoadException(
                    ex.ErrorCode,
                    ex.Stage,
                    ex.Message,
                    ex,
                    package.packageId,
                    package.packageVersion,
                    package.assembly.sha256);
            }
            catch
            {
                LoadedPackages.TryRemove(cacheKey, out _);
                throw;
            }
            loaded.Add(pack);
            LogStage(
                manifest.releaseId,
                package.packageId,
                package.packageVersion,
                package.assembly.sha256,
                "package-ready",
                packageTimer.ElapsedMilliseconds);
        }
        if (loaded.Count != selected.Count)
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.PackageIdentityMismatch,
                "package-selection",
                "The release does not provide every selected hot-update package.");
        LogStage(manifest.releaseId, null, null, null, "complete", total.ElapsedMilliseconds);
        return loaded;
    }

    private LoadedToolPack LoadPackage(
        HotUpdateToolPackageDeclaration package,
        IReadOnlyDictionary<string, byte[]> bytesByAddress)
    {
        try
        {
            Assembly assembly;
#if UNITY_EDITOR
            assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, package.assemblyName, StringComparison.Ordinal));
            if (assembly == null)
                throw new InvalidOperationException(
                    $"Editor tool assembly '{package.assemblyName}' is not loaded.");
#else
            byte[] symbols = _loadDebugSymbols && package.debugSymbols != null &&
                             !string.IsNullOrWhiteSpace(package.debugSymbols.address)
                ? bytesByAddress[package.debugSymbols.address]
                : null;
            assembly = symbols == null
                ? Assembly.Load(bytesByAddress[package.assembly.address])
                : Assembly.Load(bytesByAddress[package.assembly.address], symbols);
#endif
            if (!string.Equals(assembly.GetName().Name, package.assemblyName, StringComparison.Ordinal))
                throw new HotUpdateLoadException(
                    HotUpdateLoadErrorCodes.PackageIdentityMismatch,
                    "assembly-identity",
                    $"Loaded assembly '{assembly.GetName().Name}' does not match '{package.assemblyName}'.");
            IReadOnlyList<GameWithLLM.AgentRuntime.IAgentTool> tools;
            try
            {
                tools = AgentToolDiscovery.DiscoverFromAssembly(assembly);
            }
            catch (Exception ex)
            {
                throw new HotUpdateLoadException(
                    HotUpdateLoadErrorCodes.ToolDiscoveryFailed,
                    "tool-discovery",
                    $"Tool discovery failed for package '{package.packageId}'.",
                    ex);
            }
            return new LoadedToolPack(
                package.packageId,
                package.packageVersion,
                package.assembly.sha256,
                assembly,
                tools);
        }
        catch (HotUpdateLoadException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.AssemblyLoadFailed,
                "assembly-load",
                $"Tool package '{package.packageId}' could not be loaded.",
                ex);
        }
    }

    private static void ValidatePackageIdentity(HotUpdateToolPackageDeclaration package)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.packageId) ||
            string.IsNullOrWhiteSpace(package.assemblyName) || package.assembly == null ||
            !Version.TryParse(package.packageVersion, out _))
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.PackageIdentityMismatch,
                "package-identity",
                "A selected hot-update package has an invalid identity or version.");
    }

    private static void LoadMetadata(
        string releaseId,
        HotUpdateArtifactDeclaration metadata,
        byte[] bytes)
    {
#if !UNITY_EDITOR && ENABLE_IL2CPP
        LoadImageErrorCode result = RuntimeApi.LoadMetadataForAOTAssembly(
            bytes,
            HomologousImageMode.SuperSet);
        if (result != LoadImageErrorCode.OK &&
            result != LoadImageErrorCode.HOMOLOGOUS_ASSEMBLY_HAS_LOADED)
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.MetadataLoadFailed,
                "metadata-load",
                $"AOT metadata '{metadata.fileName}' failed to load for release '{releaseId}': {result}.");
#endif
    }

    private static void ValidateArtifact(HotUpdateArtifactDeclaration artifact, byte[] bytes)
    {
        if (bytes == null || bytes.LongLength != artifact.length)
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.ArtifactLengthMismatch,
                "artifact-validation",
                $"Artifact '{artifact.address}' length does not match its release manifest.");
        using SHA256 sha = SHA256.Create();
        string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        if (!string.Equals(hash, artifact.sha256, StringComparison.OrdinalIgnoreCase))
            throw new HotUpdateLoadException(
                HotUpdateLoadErrorCodes.ArtifactHashMismatch,
                "artifact-validation",
                $"Artifact '{artifact.address}' SHA-256 does not match its release manifest.");
    }

    private static HashSet<string> SelectedPackageKeys(HotUpdateReleaseManifest manifest) =>
        new HashSet<string>(manifest.activeTools
            .Where(tool => string.Equals(tool.source, "hot-update", StringComparison.Ordinal))
            .Select(tool => tool.packageId + "\n" + tool.packageVersion), StringComparer.Ordinal);

    private static string PackageKey(HotUpdateToolPackageDeclaration package) =>
        package.packageId + "\n" + package.packageVersion;

    private static void LogStage(
        string releaseId,
        string packageId,
        string version,
        string hash,
        string stage,
        long elapsedMilliseconds)
    {
        UnityEngine.Debug.Log(
            $"[Hot Update] releaseId='{releaseId}' packageId='{packageId ?? "-"}' " +
            $"version='{version ?? "-"}' hash='{hash ?? "-"}' stage='{stage}' " +
            $"elapsedMs={elapsedMilliseconds}.");
    }
}
