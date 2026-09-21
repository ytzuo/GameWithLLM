using System;
using System.IO;
using System.Reflection;
using HybridCLR;
using UnityEngine;

public static class HybridClrBootstrap
{
    private const string ManifestFileName = "local-hot-update-manifest.json";
    private const string RootDirectoryName = "HotUpdate";

    [Serializable]
    private sealed class LocalManifest
    {
        public string[] aotMetadataFiles;
        public string[] hotUpdateAssemblyFiles;
        public string[] debugSymbolFiles;
    }

    public static bool LoadLocalToolPacks(bool enabled)
    {
        if (!enabled)
        {
            Debug.Log("[Hot Update] HybridCLR bootstrap is disabled; continuing with AOT tools only.");
            return false;
        }

#if UNITY_EDITOR
        Debug.Log("[Hot Update] Editor uses the compiled SmokeTest assembly; runtime DLL loading is skipped.");
        return true;
#elif !ENABLE_IL2CPP
        Debug.LogWarning("[Hot Update] HybridCLR bootstrap requires an IL2CPP Player; continuing with AOT tools only.");
        return false;
#else
        string root = Path.Combine(Application.streamingAssetsPath, RootDirectoryName);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[Hot Update] Local manifest not found at '{manifestPath}'; continuing with AOT tools only.");
            return false;
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

        string[] assemblyFiles = manifest.hotUpdateAssemblyFiles ?? Array.Empty<string>();
        string[] symbolFiles = manifest.debugSymbolFiles ?? Array.Empty<string>();
        for (int index = 0; index < assemblyFiles.Length; index++)
        {
            byte[] assemblyBytes = ReadArtifact(root, assemblyFiles[index]);
            byte[] symbolBytes = index < symbolFiles.Length &&
                                 !string.IsNullOrWhiteSpace(symbolFiles[index])
                ? ReadArtifact(root, symbolFiles[index])
                : null;
            Assembly assembly = symbolBytes == null
                ? Assembly.Load(assemblyBytes)
                : Assembly.Load(assemblyBytes, symbolBytes);
            Debug.Log($"[Hot Update] Loaded local tool assembly '{assembly.GetName().Name}'.");
        }
        return assemblyFiles.Length > 0;
#endif
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
