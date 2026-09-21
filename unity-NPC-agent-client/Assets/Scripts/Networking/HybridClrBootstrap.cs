using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        public string[] aotMetadataFiles;
        public LocalToolPackage[] toolPackages;
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

    public static IReadOnlyList<LoadedToolPack> LoadLocalToolPacks(bool enabled)
    {
        if (!enabled)
        {
            Debug.Log("[Hot Update] HybridCLR bootstrap is disabled; continuing with AOT tools only.");
            return Array.Empty<LoadedToolPack>();
        }

#if UNITY_EDITOR
        Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, SmokeAssemblyName, StringComparison.Ordinal));
        if (assembly == null)
            throw new InvalidOperationException($"Editor tool assembly '{SmokeAssemblyName}' is not loaded.");
        string hash = ComputeEditorAssemblyHash(assembly);
        Debug.Log("[Hot Update] Editor uses the compiled SmokeTest assembly for explicit package discovery.");
        return new[]
        {
            new LoadedToolPack(SmokePackageId, SmokePackageVersion, hash, assembly)
        };
#elif !ENABLE_IL2CPP
        Debug.LogWarning("[Hot Update] HybridCLR bootstrap requires an IL2CPP Player; continuing with AOT tools only.");
        return Array.Empty<LoadedToolPack>();
#else
        string root = Path.Combine(Application.streamingAssetsPath, RootDirectoryName);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[Hot Update] Local manifest not found at '{manifestPath}'; continuing with AOT tools only.");
            return Array.Empty<LoadedToolPack>();
        }

        LocalManifest manifest = JsonUtility.FromJson<LocalManifest>(File.ReadAllText(manifestPath));
        if (manifest == null)
            throw new InvalidDataException("HybridCLR local manifest is invalid.");

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
        foreach (LocalToolPackage package in manifest.toolPackages ?? Array.Empty<LocalToolPackage>())
        {
            if (package == null || string.IsNullOrWhiteSpace(package.packageId) ||
                string.IsNullOrWhiteSpace(package.packageVersion) ||
                string.IsNullOrWhiteSpace(package.assemblyName))
            {
                throw new InvalidDataException("Local tool package metadata is incomplete.");
            }
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
        return loadedPacks;
#endif
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
