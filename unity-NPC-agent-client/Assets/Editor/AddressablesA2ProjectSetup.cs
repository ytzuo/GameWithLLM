using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class AddressablesA2ProjectSetup
{
    private const string StagingRoot = "Assets/Content/HotUpdate";
    private const string LocalRoot = "Assets/StreamingAssets/HotUpdate";
    private const string ManifestPath = StagingRoot + "/release-manifest.json";
    private const string ContentVersion = "2026.09.001";

    [MenuItem("GameWithLLM/Hot Update/Stage And Configure Addressables A2")]
    public static void StageAndConfigure()
    {
        HybridClrProjectSetup.StageLocalArtifacts(EditorUserBuildSettings.development);
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Content", "HotUpdate", "Metadata"));
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "Content", "HotUpdate", "ToolPacks"));

        JObject local = JObject.Parse(File.ReadAllText(LocalRoot + "/local-hot-update-manifest.json"));
        string releaseId = (string)local["releaseId"] ?? throw new InvalidDataException("Local releaseId is missing.");
        string playerBuildId = "windows-x64-" + Application.version;
        var metadata = new JArray();
        foreach (string file in local["aotMetadataFiles"]?.Values<string>() ?? Enumerable.Empty<string>())
        {
            string destination = StagingRoot + "/Metadata/" + file;
            File.Copy(LocalRoot + "/" + file, destination, true);
            metadata.Add(Artifact(destination, $"hotfix/aot/{playerBuildId}/{AssemblyId(file)}"));
        }

        var packages = new JArray();
        foreach (JObject package in local["toolPackages"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string packageId = (string)package["packageId"];
            string packageVersion = (string)package["packageVersion"];
            string assemblyFile = (string)package["assemblyFile"];
            string assemblyPath = StagingRoot + "/ToolPacks/" + assemblyFile;
            File.Copy(LocalRoot + "/" + assemblyFile, assemblyPath, true);
            packages.Add(new JObject
            {
                ["packageId"] = packageId,
                ["packageVersion"] = packageVersion,
                ["assemblyName"] = package["assemblyName"],
                ["assembly"] = Artifact(
                    assemblyPath,
                    $"hotfix/tools/{packageId}/{packageVersion}/assembly"),
                // 单一 A2 候选会同时发布到三个 channel；为保证 Production
                // 不携带调试符号，当前公共候选统一不发布 PDB。
                ["debugSymbols"] = null
            });
        }

        var catalogs = new JArray
        {
            Catalog("tool_metadata.zh-CN", "Assets/Content/Catalogs/tool_metadata.zh-CN.json", "config/tool-metadata/zh-CN"),
            Catalog("agent_messages.zh-CN", "Assets/Content/Catalogs/agent_messages.zh-CN.json", "config/agent-messages/zh-CN"),
            Catalog("ui.zh-CN", "Assets/Content/Catalogs/ui.zh-CN.json", "config/ui/zh-CN")
        };
        var manifest = new JObject
        {
            ["schemaVersion"] = 1,
            ["releaseId"] = releaseId,
            ["contentVersion"] = ContentVersion,
            ["toolSetVersion"] = local["toolSetVersion"],
            ["catalogVersion"] = local["catalogVersion"],
            ["playerBuildId"] = playerBuildId,
            ["minPlayerVersion"] = local["minPlayerVersion"],
            ["maxPlayerVersion"] = local["maxPlayerVersion"],
            ["catalogs"] = catalogs,
            ["aotMetadata"] = metadata,
            ["toolPackages"] = packages,
            ["activeTools"] = local["activeTools"],
            ["retiredTools"] = local["retiredTools"],
            ["toolHistory"] = local["toolHistory"]
        };
        JObject publishedManifest = (JObject)manifest.DeepClone();
        foreach (JProperty sourcePath in publishedManifest
                     .Descendants()
                     .OfType<JProperty>()
                     .Where(property => property.Name == "sourcePath")
                     .ToArray())
            sourcePath.Remove();
        File.WriteAllText(
            ManifestPath,
            publishedManifest.ToString(Formatting.Indented) + Environment.NewLine);
        // 旧目录只是 H2-H5 的可重建中转站。A2 复制完成后立即移除，避免
        // StreamingAssets 把同一候选再次嵌入 Player。
        RemoveLegacyLocalStaging();
        AssetDatabase.Refresh();

        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        RemovePublishedDebugSymbols(settings);
        ConfigureEntry(settings, ManifestPath, "Remote_ClientConfig",
            AddressableHotUpdateReleaseLoader.ManifestAddress, "content.release-manifest");
        foreach (JObject catalog in catalogs.Children<JObject>())
            ConfigureEntry(settings, SourcePath(catalog), "Remote_ClientConfig",
                (string)catalog["address"], "content.client-config");
        foreach (JObject artifact in metadata.Children<JObject>())
            ConfigureEntry(settings, SourcePath(artifact), "Remote_HotfixMetadata",
                (string)artifact["address"], "content.hotfix-metadata");
        foreach (JObject package in packages.Children<JObject>())
        {
            JObject assembly = (JObject)package["assembly"];
            ConfigureEntry(settings, SourcePath(assembly), "Remote_ToolPacks",
                (string)assembly["address"], "content.hotfix-tools");
            if (package["debugSymbols"] is JObject debug)
                ConfigureEntry(settings, SourcePath(debug), "Remote_ToolPacks",
                    (string)debug["address"], "content.hotfix-symbols");
        }
        SetReleaseId(settings, releaseId);
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        AssetDatabase.SaveAssets();
        Verify();
        Debug.Log($"[Content] Addressables A2 release '{releaseId}' staged and configured.");
    }

    [MenuItem("GameWithLLM/Hot Update/Verify Addressables A2")]
    public static void Verify()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        if (!File.Exists(ManifestPath))
            throw new FileNotFoundException("A2 release manifest is missing.", ManifestPath);
        JObject manifest = JObject.Parse(File.ReadAllText(ManifestPath));
        if ((int?)manifest["schemaVersion"] != 1 ||
            (string)manifest["contentVersion"] != (string)manifest["catalogVersion"])
            throw new InvalidDataException("A2 release manifest version envelope is invalid.");
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (JObject artifact in EnumerateArtifacts(manifest))
        {
            string address = (string)artifact["address"];
            AddressableAssetEntry entry = FindEntryByAddress(settings, address);
            string path = entry?.AssetPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !addresses.Add(address))
                throw new InvalidDataException($"A2 artifact '{address}' is missing or duplicated.");
            byte[] bytes = File.ReadAllBytes(path);
            if ((long?)artifact["length"] != bytes.LongLength ||
                !string.Equals((string)artifact["sha256"], Sha256(bytes), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"A2 artifact '{address}' does not match its manifest.");
        }
        AddressableAssetEntry releaseEntry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(ManifestPath));
        if (releaseEntry == null || releaseEntry.parentGroup.Name != "Remote_ClientConfig" ||
            releaseEntry.address != AddressableHotUpdateReleaseLoader.ManifestAddress)
            throw new InvalidDataException("A2 release manifest Addressable entry is invalid.");
        Debug.Log($"[Content] Addressables A2 verified: {addresses.Count} immutable candidate artifacts.");
    }

    public static void StageAndConfigureFromCommandLine() => RunCommand(StageAndConfigure);
    public static void VerifyFromCommandLine() => RunCommand(Verify);
    public static void BuildAllWindowsProfilesFromCommandLine() => RunCommand(() =>
    {
        Verify();
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        string originalProfile = settings.activeProfileId;
        try
        {
            foreach (string profileName in new[] { "LocalDevelopment", "QA", "Production" })
            {
                settings.activeProfileId = settings.profileSettings.GetProfileId(profileName);
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                if (!string.IsNullOrWhiteSpace(result.Error))
                    throw new InvalidOperationException(
                        $"A2 Addressables build for '{profileName}' failed: {result.Error}");
                string releaseId = settings.profileSettings.GetValueByName(
                    settings.activeProfileId,
                    "ReleaseId");
                Debug.Log(
                    $"[Content] Built A2 Windows profile '{profileName}' release '{releaseId}'.");
            }
        }
        finally
        {
            settings.activeProfileId = originalProfile;
            AssetDatabase.SaveAssets();
        }
    });

    public static void StripDebugSymbolsFromCommandLine() => RunCommand(() =>
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        JObject manifest = JObject.Parse(File.ReadAllText(ManifestPath));
        foreach (JObject package in manifest["toolPackages"].Children<JObject>())
            package["debugSymbols"] = null;
        File.WriteAllText(ManifestPath, manifest.ToString(Formatting.Indented) + Environment.NewLine);
        RemovePublishedDebugSymbols(settings);
        AssetDatabase.Refresh();
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        AssetDatabase.SaveAssets();
        Verify();
    });

    public static void BuildSmokePlayerFromCommandLine() => RunCommand(() =>
    {
        // 发布 staging 是独立步骤；Player 构建只消费并验证已经冻结的候选，
        // 避免失败重试时悄悄重写 release 内容。
        Verify();
        RemoveLegacyLocalStaging();
        AssetDatabase.Refresh();
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
        AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult contentResult);
        if (!string.IsNullOrWhiteSpace(contentResult.Error))
            throw new InvalidOperationException("A2 Addressables build failed: " + contentResult.Error);
        string output = Path.GetFullPath(Path.Combine(
            Application.dataPath,
            "..",
            "Builds",
            "AddressablesA2",
            "GameWithLLM.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("A2 Windows Player build failed: " + report.summary.result);
        Debug.Log($"[Content] A2 Windows Player built at '{output}'.");
    });

    private static JObject Catalog(string id, string path, string address)
    {
        JObject artifact = Artifact(path, address);
        artifact["catalogId"] = id;
        return artifact;
    }

    private static JObject Artifact(string path, string address)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new JObject
        {
            ["address"] = address,
            ["fileName"] = Path.GetFileName(path),
            ["length"] = bytes.LongLength,
            ["sha256"] = Sha256(bytes),
            ["sourcePath"] = path.Replace('\\', '/')
        };
    }

    private static IEnumerable<JObject> EnumerateArtifacts(JObject manifest)
    {
        foreach (JObject item in manifest["catalogs"].Children<JObject>()) yield return item;
        foreach (JObject item in manifest["aotMetadata"].Children<JObject>()) yield return item;
        foreach (JObject package in manifest["toolPackages"].Children<JObject>())
        {
            yield return (JObject)package["assembly"];
            if (package["debugSymbols"] is JObject debug) yield return debug;
        }
    }

    private static string SourcePath(JObject artifact) => (string)artifact["sourcePath"];

    private static AddressableAssetEntry FindEntryByAddress(
        AddressableAssetSettings settings,
        string address) => settings.groups
        .Where(group => group != null)
        .SelectMany(group => group.entries)
        .SingleOrDefault(entry => string.Equals(entry.address, address, StringComparison.Ordinal));

    private static string AssemblyId(string file) =>
        file.EndsWith(".dll.bytes", StringComparison.Ordinal)
            ? file.Substring(0, file.Length - ".dll.bytes".Length)
            : Path.GetFileNameWithoutExtension(file);

    private static string Sha256(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void ConfigureEntry(
        AddressableAssetSettings settings,
        string path,
        string groupName,
        string address,
        string label)
    {
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrEmpty(guid))
            throw new InvalidOperationException($"A2 source asset is not imported: '{path}'.");
        AddressableAssetGroup group = settings.FindGroup(groupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{groupName}' is missing.");
        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = address;
        entry.SetLabel(label, true, false, false);
    }

    private static void SetReleaseId(AddressableAssetSettings settings, string releaseId)
    {
        foreach (string profileName in new[] { "LocalDevelopment", "QA", "Production" })
        {
            string profile = settings.profileSettings.GetProfileId(profileName);
            if (!string.IsNullOrEmpty(profile))
                settings.profileSettings.SetValue(profile, "ReleaseId", releaseId);
        }
    }

    private static void RemoveLegacyLocalStaging()
    {
        if (Directory.Exists(LocalRoot))
            FileUtil.DeleteFileOrDirectory(LocalRoot);
        if (File.Exists(LocalRoot + ".meta"))
            FileUtil.DeleteFileOrDirectory(LocalRoot + ".meta");
    }

    private static void RemovePublishedDebugSymbols(AddressableAssetSettings settings)
    {
        AddressableAssetGroup group = settings.FindGroup("Remote_ToolPacks");
        if (group == null)
            return;
        foreach (AddressableAssetEntry entry in group.entries
                     .Where(item => item.labels.Contains("content.hotfix-symbols"))
                     .ToArray())
        {
            string path = entry.AssetPath;
            settings.RemoveAssetEntry(entry.guid, false);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                FileUtil.DeleteFileOrDirectory(path);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path + ".meta"))
                FileUtil.DeleteFileOrDirectory(path + ".meta");
        }
    }

    private static void RunCommand(Action action)
    {
        try { action(); EditorApplication.Exit(0); }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
}
