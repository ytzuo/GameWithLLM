using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GameWithLLM.AgentRuntime;
using HybridCLR;
using UnityEngine;

public sealed class LoadedToolPack
{
    public string PackageId { get; }
    public string PackageVersion { get; }
    public string AssemblyHash { get; }
    public Assembly Assembly { get; }

    public LoadedToolPack(
        string packageId,
        string packageVersion,
        string assemblyHash,
        Assembly assembly)
    {
        PackageId = packageId;
        PackageVersion = packageVersion;
        AssemblyHash = assemblyHash;
        Assembly = assembly;
    }
}

public sealed class LoadedToolSetRelease
{
    public string ReleaseId { get; }
    public string ToolSetVersion { get; }
    public string CatalogVersion { get; }
    public IReadOnlyList<ToolReleaseDeclaration> ActiveTools { get; }
    public IReadOnlyList<RetiredToolDeclaration> RetiredTools { get; }
    public IReadOnlyList<ToolHistoryDeclaration> History { get; }
    public IReadOnlyList<LoadedToolPack> ToolPacks { get; }

    public LoadedToolSetRelease(
        string releaseId,
        string toolSetVersion,
        string catalogVersion,
        IReadOnlyList<ToolReleaseDeclaration> activeTools,
        IReadOnlyList<RetiredToolDeclaration> retiredTools,
        IReadOnlyList<ToolHistoryDeclaration> history,
        IReadOnlyList<LoadedToolPack> toolPacks)
    {
        ReleaseId = releaseId;
        ToolSetVersion = toolSetVersion;
        CatalogVersion = catalogVersion;
        ActiveTools = activeTools;
        RetiredTools = retiredTools;
        History = history;
        ToolPacks = toolPacks;
    }

    public ToolSetCandidate BuildCandidate(IReadOnlyList<IAgentTool> builtinTools)
    {
        var byAssemblyAndName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (IAgentTool tool in builtinTools ?? Array.Empty<IAgentTool>())
            AddResolvedTool(byAssemblyAndName, tool);
        foreach (LoadedToolPack package in ToolPacks)
        {
            foreach (IAgentTool tool in AgentToolDiscovery.DiscoverFromAssembly(package.Assembly))
                AddResolvedTool(byAssemblyAndName, tool);
        }

        var candidates = new List<ToolCandidate>(ActiveTools.Count);
        foreach (ToolReleaseDeclaration declaration in ActiveTools)
        {
            if (string.Equals(declaration.source, "hot-update", StringComparison.Ordinal))
            {
                LoadedToolPack package = ToolPacks.SingleOrDefault(candidate =>
                    string.Equals(candidate.PackageId, declaration.packageId, StringComparison.Ordinal) &&
                    string.Equals(candidate.PackageVersion, declaration.packageVersion, StringComparison.Ordinal));
                if (package == null ||
                    !string.Equals(package.Assembly.GetName().Name, declaration.assemblyName, StringComparison.Ordinal) ||
                    !string.Equals(package.AssemblyHash, declaration.assemblyHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Release package identity does not match tool '{declaration.name}'.");
            }
            string key = declaration.assemblyName + "\n" + declaration.name;
            if (!byAssemblyAndName.TryGetValue(key, out IAgentTool tool))
                throw new InvalidDataException(
                    $"Release tool '{declaration.name}' could not be resolved from '{declaration.assemblyName}'.");
            candidates.Add(new ToolCandidate(declaration, tool));
        }
        return new ToolSetCandidate(
            ReleaseId,
            ToolSetVersion,
            CatalogVersion,
            candidates,
            RetiredTools,
            History);
    }

    private static void AddResolvedTool(IDictionary<string, IAgentTool> tools, IAgentTool tool)
    {
        string key = tool.GetType().Assembly.GetName().Name + "\n" + tool.Descriptor.Name;
        if (tools.ContainsKey(key))
            throw new InvalidOperationException($"Assembly contains duplicate resolved tool '{tool.Descriptor.Name}'.");
        tools.Add(key, tool);
    }
}

public static class HybridClrBootstrap
{
    private const string ManifestFileName = "local-hot-update-manifest.json";
    private const string RootDirectoryName = "HotUpdate";
    public const string SmokePackageId = "smoke-test";
    public const string SmokePackageVersion = "1.0.0";
    public const string SmokeAssemblyName = "GameWithLLM.Tools.Pack.SmokeTest";

    [Serializable]
    private sealed class LocalManifest
    {
        public string releaseId;
        public string toolSetVersion;
        public string catalogVersion;
        public string minPlayerVersion;
        public string maxPlayerVersion;
        public string[] aotMetadataFiles;
        public LocalToolPackage[] toolPackages;
        public ToolReleaseDeclaration[] activeTools;
        public RetiredToolDeclaration[] retiredTools;
        public ToolHistoryDeclaration[] toolHistory;
    }

    [Serializable]
    private sealed class LocalToolPackage
    {
        public string packageId;
        public string packageVersion;
        public string assemblyName;
        public string assemblyFile;
        public string assemblyHash;
        public string debugSymbolFile;
    }

    public static LoadedToolSetRelease LoadLocalRelease(bool enabled)
    {
        if (!enabled)
        {
            Debug.Log("[Hot Update] HybridCLR bootstrap is disabled; continuing with AOT tools only.");
            return null;
        }

#if UNITY_EDITOR
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, SmokeAssemblyName, StringComparison.Ordinal));
        if (assembly == null)
            throw new InvalidOperationException($"Editor tool assembly '{SmokeAssemblyName}' is not loaded.");
        string hash = ComputeEditorAssemblyHash(assembly);
        Debug.Log("[Hot Update] Editor uses the compiled SmokeTest assembly for explicit package discovery.");
        var pack = new LoadedToolPack(SmokePackageId, SmokePackageVersion, hash, assembly);
        return CreateEditorRelease(pack);
#elif !ENABLE_IL2CPP
        Debug.LogWarning("[Hot Update] HybridCLR bootstrap requires an IL2CPP Player; continuing with AOT tools only.");
        return null;
#else
        string root = Path.Combine(Application.streamingAssetsPath, RootDirectoryName);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[Hot Update] Local manifest not found at '{manifestPath}'; continuing with AOT tools only.");
            return null;
        }

        LocalManifest manifest = JsonUtility.FromJson<LocalManifest>(File.ReadAllText(manifestPath));
        if (manifest == null)
            throw new InvalidDataException("HybridCLR local manifest is invalid.");
        ValidateManifestHeader(manifest);

        foreach (string fileName in manifest.aotMetadataFiles ?? Array.Empty<string>())
        {
            byte[] bytes = ReadArtifact(root, fileName);
            LoadImageErrorCode result = RuntimeApi.LoadMetadataForAOTAssembly(
                bytes,
                HomologousImageMode.SuperSet);
            if (result != LoadImageErrorCode.OK &&
                result != LoadImageErrorCode.HOMOLOGOUS_ASSEMBLY_HAS_LOADED)
            {
                throw new InvalidOperationException(
                    $"Failed to load AOT metadata '{fileName}': {result}.");
            }
        }

        var loadedPacks = new List<LoadedToolPack>();
        ToolReleaseDeclaration[] activeTools = manifest.activeTools ?? Array.Empty<ToolReleaseDeclaration>();
        var selectedPackages = new HashSet<string>(
            activeTools
                .Where(tool => string.Equals(tool.source, "hot-update", StringComparison.Ordinal))
                .Select(tool => tool.packageId + "\n" + tool.packageVersion),
            StringComparer.Ordinal);
        foreach (LocalToolPackage package in manifest.toolPackages ?? Array.Empty<LocalToolPackage>())
        {
            if (package == null || string.IsNullOrWhiteSpace(package.packageId) ||
                string.IsNullOrWhiteSpace(package.packageVersion) ||
                string.IsNullOrWhiteSpace(package.assemblyName))
            {
                throw new InvalidDataException("Local tool package metadata is incomplete.");
            }
            string packageKey = package.packageId + "\n" + package.packageVersion;
            if (!selectedPackages.Contains(packageKey))
                continue;
            byte[] assemblyBytes = ReadArtifact(root, package.assemblyFile);
            string actualHash = ComputeSha256(assemblyBytes);
            if (!string.Equals(actualHash, package.assemblyHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Tool package '{package.packageId}' assembly hash does not match its manifest.");
            }
            byte[] symbolBytes = !string.IsNullOrWhiteSpace(package.debugSymbolFile)
                ? ReadArtifact(root, package.debugSymbolFile)
                : null;
            Assembly assembly = symbolBytes == null
                ? Assembly.Load(assemblyBytes)
                : Assembly.Load(assemblyBytes, symbolBytes);
            if (!string.Equals(
                    assembly.GetName().Name,
                    package.assemblyName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Loaded assembly '{assembly.GetName().Name}' does not match manifest name " +
                    $"'{package.assemblyName}'.");
            }
            loadedPacks.Add(new LoadedToolPack(
                package.packageId,
                package.packageVersion,
                actualHash,
                assembly));
            Debug.Log(
                $"[Hot Update] Loaded local tool package '{package.packageId}' " +
                $"version '{package.packageVersion}'.");
        }
        if (loadedPacks.Count != selectedPackages.Count)
            throw new InvalidDataException("The release does not provide every selected hot-update package.");
        return new LoadedToolSetRelease(
            manifest.releaseId,
            manifest.toolSetVersion,
            manifest.catalogVersion,
            activeTools,
            manifest.retiredTools ?? Array.Empty<RetiredToolDeclaration>(),
            manifest.toolHistory ?? Array.Empty<ToolHistoryDeclaration>(),
            loadedPacks);
#endif
    }

    private static LoadedToolSetRelease CreateEditorRelease(LoadedToolPack pack)
    {
        var tools = new List<IAgentTool>();
        tools.AddRange(AgentToolDiscovery.DiscoverBuiltinTools());
        tools.AddRange(AgentToolDiscovery.DiscoverFromAssembly(pack.Assembly));
        var declarations = new List<ToolReleaseDeclaration>(tools.Count);
        var history = new List<ToolHistoryDeclaration>(tools.Count);
        foreach (IAgentTool tool in tools)
        {
            bool hot = tool.GetType().Assembly == pack.Assembly;
            var declaration = new ToolReleaseDeclaration
            {
                name = tool.Descriptor.Name,
                toolIdentity = tool.Descriptor.Name,
                source = hot ? "hot-update" : "builtin",
                implementationVersion = "1.0.0",
                contractVersion = "1.0.0",
                packageId = hot ? pack.PackageId : null,
                packageVersion = hot ? pack.PackageVersion : null,
                assemblyName = tool.GetType().Assembly.GetName().Name,
                assemblyHash = hot ? pack.AssemblyHash : null,
                schemaHash = ToolSetValidator.ComputeSchemaHash(tool.Descriptor.InputSchemaJson)
            };
            declarations.Add(declaration);
            history.Add(ToolSetCandidateFactory.ToHistory(declaration, "h4-editor-local"));
        }
        return new LoadedToolSetRelease(
            "h4-editor-local",
            "1.0.0",
            "embedded-1",
            declarations,
            Array.Empty<RetiredToolDeclaration>(),
            history,
            new[] { pack });
    }

    private static void ValidateManifestHeader(LocalManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.releaseId) ||
            string.IsNullOrWhiteSpace(manifest.toolSetVersion) ||
            string.IsNullOrWhiteSpace(manifest.catalogVersion) ||
            string.IsNullOrWhiteSpace(manifest.minPlayerVersion) ||
            string.IsNullOrWhiteSpace(manifest.maxPlayerVersion))
            throw new InvalidDataException("H4 release manifest header is incomplete.");
        if (CompareNumericVersions(Application.version, manifest.minPlayerVersion) < 0 ||
            CompareNumericVersions(Application.version, manifest.maxPlayerVersion) > 0)
            throw new InvalidDataException(
                $"Release '{manifest.releaseId}' is not compatible with Player '{Application.version}'.");
    }

    private static int CompareNumericVersions(string left, string right)
    {
        if (!Version.TryParse(left, out Version a) || !Version.TryParse(right, out Version b))
            throw new InvalidDataException("Player compatibility versions must be numeric dotted versions.");
        return a.CompareTo(b);
    }

    private static string ComputeEditorAssemblyHash(Assembly assembly)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
                return ComputeSha256(File.ReadAllBytes(assembly.Location));
        }
        catch (NotSupportedException)
        {
        }
        return ComputeSha256(Encoding.UTF8.GetBytes(
            assembly.FullName + "|" + assembly.ManifestModule.ModuleVersionId.ToString("N")));
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha256 = SHA256.Create())
            return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static byte[] ReadArtifact(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.GetFileName(fileName) != fileName)
        {
            throw new InvalidDataException($"Invalid local hot-update artifact name '{fileName}'.");
        }
        string path = Path.Combine(root, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("Local hot-update artifact is missing.", path);
        return File.ReadAllBytes(path);
    }
}
