using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using GameWithLLM.AgentRuntime;
using HybridCLR.Editor;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.Installer;
using HybridCLR.Editor.Settings;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HybridClrProjectSetup
{
    public const string SmokeAssemblyName = "GameWithLLM.Tools.Pack.SmokeTest";
    public const string SmokePackageId = "smoke-test";
    public const string SmokePackageVersion = "1.4.0";
    public static bool IsGenerating { get; private set; }

    private static readonly string[] PatchAotAssemblies =
    {
        "mscorlib",
        "System",
        "System.Core",
        "GameWithLLM.AgentRuntime",
        "GameWithLLM.Client.Gameplay",
        "GameWithLLM.Client.ToolFramework",
        "Newtonsoft.Json"
    };

    public static void Configure()
    {
        PlayerSettings.SetScriptingBackend(
            NamedBuildTarget.Standalone,
            ScriptingImplementation.IL2CPP);
        PlayerSettings.SetArchitecture(BuildTargetGroup.Standalone, 1);
        HybridCLRSettings settings = HybridCLRSettings.LoadOrCreate();
        settings.enable = true;
        settings.hybridclrRepoURL = "https://github.com/focus-creative-games/hybridclr";
        settings.il2cppPlusRepoURL = "https://github.com/focus-creative-games/il2cpp_plus";
        settings.hotUpdateAssemblyDefinitions =
            Array.Empty<UnityEditorInternal.AssemblyDefinitionAsset>();
        settings.hotUpdateAssemblies = new[] { SmokeAssemblyName };
        settings.preserveHotUpdateAssemblies = Array.Empty<string>();
        settings.patchAOTAssemblies = PatchAotAssemblies;
        HybridCLRSettings.Save();

        AddressableAssetSettings addressableSettings =
            AddressableAssetSettingsDefaultObject.Settings;
        if (addressableSettings != null)
        {
            // Artifact generation must not trigger an implicit Addressables build.
            addressableSettings.BuildAddressablesWithPlayerBuild =
                AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            EditorUtility.SetDirty(addressableSettings);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Hot Update] HybridCLR configured for Windows x86_64 IL2CPP and SmokeTest.");
    }

    public static void GenerateAndStageForPipeline(bool includeDebugSymbols)
    {
        Configure();
        // Local diagnostics may retain portable PDB symbols; production candidates do not.
        EditorUserBuildSettings.development = includeDebugSymbols;
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 &&
            !EditorUserBuildSettings.SwitchActiveBuildTarget(
                BuildTargetGroup.Standalone,
                BuildTarget.StandaloneWindows64))
        {
            throw new BuildFailedException("Could not switch to StandaloneWindows64.");
        }

        IsGenerating = true;
        try
        {
            PrebuildCommand.GenerateAll();
        }
        finally
        {
            IsGenerating = false;
        }
        StageLocalArtifacts(includeDebugSymbols);
    }

    public static void StageLocalArtifacts(bool includeDebugSymbols)
    {
        BuildTarget target = BuildTarget.StandaloneWindows64;
        string hotUpdateSource = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target);
        string metadataSource = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
        string destination = Path.Combine(Application.streamingAssetsPath, "HotUpdate");
        Directory.CreateDirectory(destination);

        string catalogSource = Path.Combine(Application.dataPath, "Content", "Catalogs");
        foreach (string catalogFile in new[]
                 {
                     "tool_metadata.zh-CN.json",
                     "agent_messages.zh-CN.json",
                     "ui.zh-CN.json"
                 })
        {
            string source = Path.Combine(catalogSource, catalogFile);
            if (!File.Exists(source))
                throw new FileNotFoundException($"Required content catalog is missing: '{catalogFile}'.", source);
            File.Copy(source, Path.Combine(destination, catalogFile), true);
        }

        var metadataFiles = new List<string>();
        foreach (string assemblyName in PatchAotAssemblies)
        {
            string source = Path.Combine(metadataSource, assemblyName + ".dll");
            if (!File.Exists(source))
                throw new FileNotFoundException($"Generated AOT metadata is missing for '{assemblyName}'.", source);
            string fileName = assemblyName + ".dll.bytes";
            File.Copy(source, Path.Combine(destination, fileName), true);
            metadataFiles.Add(fileName);
        }

        string hotDllSource = Path.Combine(hotUpdateSource, SmokeAssemblyName + ".dll");
        if (!File.Exists(hotDllSource))
            throw new FileNotFoundException("Generated SmokeTest hot-update DLL is missing.", hotDllSource);
        string hotDllFile = SmokeAssemblyName + ".dll.bytes";
        File.Copy(hotDllSource, Path.Combine(destination, hotDllFile), true);
        string hotDllHash = ComputeSha256(File.ReadAllBytes(hotDllSource));

        string pdbFile = string.Empty;
        string pdbSource = Path.Combine(hotUpdateSource, SmokeAssemblyName + ".pdb");
        string stagedPdb = Path.Combine(destination, SmokeAssemblyName + ".pdb.bytes");
        if (includeDebugSymbols && File.Exists(pdbSource))
        {
            pdbFile = Path.GetFileName(stagedPdb);
            File.Copy(pdbSource, stagedPdb, true);
        }
        else if (File.Exists(stagedPdb))
        {
            File.Delete(stagedPdb);
        }

        Assembly smokeAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(candidate =>
            string.Equals(candidate.GetName().Name, SmokeAssemblyName, StringComparison.Ordinal));
        var releaseTools = new List<IAgentTool>();
        releaseTools.AddRange(AgentToolDiscovery.DiscoverBuiltinTools());
        releaseTools.AddRange(AgentToolDiscovery.DiscoverFromAssembly(smokeAssembly));
        const string releaseId = "h7-production-1.3.0";
        var activeTools = releaseTools.Select(tool =>
        {
            bool hot = string.Equals(
                tool.GetType().Assembly.GetName().Name,
                SmokeAssemblyName,
                StringComparison.Ordinal);
            return new ToolReleaseDeclaration
            {
                name = tool.Descriptor.Name,
                toolIdentity = tool.Descriptor.Name,
                source = hot ? "hot-update" : "builtin",
                implementationVersion = hot ? "5.0.0" : "1.0.0",
                contractVersion = "1.0.0",
                packageId = hot ? SmokePackageId : null,
                packageVersion = hot ? SmokePackageVersion : null,
                assemblyName = tool.GetType().Assembly.GetName().Name,
                assemblyHash = hot ? hotDllHash : null,
                schemaHash = ToolSetValidator.ComputeSchemaHash(tool.Descriptor.InputSchemaJson)
            };
        }).OrderBy(tool => tool.name, StringComparer.Ordinal).ToArray();
        string repositoryRoot = Directory.GetParent(
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath)?.FullName
            ?? Application.dataPath;
        ToolHistoryDeclaration[] history = LoadAndValidateHistoryLedger(
            Path.Combine(repositoryRoot, "Docs", "History", "HotUpdate", "Baselines", "h4-tool-history.json"),
            activeTools,
            Array.Empty<RetiredToolDeclaration>());

        string manifest = JsonConvert.SerializeObject(new
        {
            releaseId,
            toolSetVersion = "5.0.0",
            catalogVersion = "2026.09.001",
            minPlayerVersion = Application.version,
            maxPlayerVersion = Application.version,
            aotMetadataFiles = metadataFiles,
            toolPackages = new[]
            {
                new
                {
                    packageId = SmokePackageId,
                    packageVersion = SmokePackageVersion,
                    assemblyName = SmokeAssemblyName,
                    assemblyFile = hotDllFile,
                    assemblyHash = hotDllHash,
                    debugSymbolFile = pdbFile
                }
            },
            activeTools,
            retiredTools = Array.Empty<RetiredToolDeclaration>(),
            toolHistory = history
        }, Formatting.Indented);
        File.WriteAllText(
            Path.Combine(destination, "local-hot-update-manifest.json"),
            manifest + Environment.NewLine);
        AssetDatabase.Refresh();
        Debug.Log($"[Hot Update] Staged local HybridCLR artifacts at '{destination}'.");
    }

    private static ToolHistoryDeclaration[] LoadAndValidateHistoryLedger(
        string path,
        IReadOnlyList<ToolReleaseDeclaration> activeTools,
        IReadOnlyList<RetiredToolDeclaration> retiredTools)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The committed tool history ledger is missing.", path);
        ToolHistoryDeclaration[] history =
            JsonConvert.DeserializeObject<ToolHistoryDeclaration[]>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The tool history ledger is invalid.");
        var active = activeTools.ToDictionary(tool => tool.name, StringComparer.Ordinal);
        var retired = retiredTools.ToDictionary(tool => tool.name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ToolHistoryDeclaration record in history)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.name) || !seen.Add(record.name))
                throw new InvalidDataException("The tool history ledger contains a null or duplicate tool record.");
            if (active.TryGetValue(record.name, out ToolReleaseDeclaration declaration))
            {
                if (!string.Equals(record.toolIdentity, declaration.toolIdentity, StringComparison.Ordinal) ||
                    !string.Equals(record.source, declaration.source, StringComparison.Ordinal) ||
                    !string.Equals(record.implementationVersion, declaration.implementationVersion, StringComparison.Ordinal) ||
                    !string.Equals(record.contractVersion, declaration.contractVersion, StringComparison.Ordinal) ||
                    !string.Equals(record.schemaHash, declaration.schemaHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.assemblyHash ?? string.Empty, declaration.assemblyHash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(record.retiredInToolSetVersion))
                    throw new InvalidDataException($"Tool history ledger does not match active tool '{record.name}'.");
            }
            else if (retired.TryGetValue(record.name, out RetiredToolDeclaration tombstone))
            {
                if (!string.Equals(record.toolIdentity, tombstone.toolIdentity, StringComparison.Ordinal) ||
                    !string.Equals(record.retiredInToolSetVersion, tombstone.retiredInToolSetVersion, StringComparison.Ordinal))
                    throw new InvalidDataException($"Tool history ledger does not match retired tool '{record.name}'.");
            }
            else
            {
                throw new InvalidDataException(
                    $"Historical tool '{record.name}' must remain active or have a release tombstone.");
            }
        }
        foreach (string name in active.Keys.Concat(retired.Keys))
        {
            if (!seen.Contains(name))
                throw new InvalidDataException($"Release tool '{name}' is missing from the committed history ledger.");
        }
        return history;
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha256 = SHA256.Create())
            return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

}

public sealed class HybridClrLocalArtifactsBuildProcessor : IPreprocessBuildWithReport
{
    public int callbackOrder => -1000;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (HybridClrProjectSetup.IsGenerating ||
            report.summary.platform != BuildTarget.StandaloneWindows64)
            return;
        string a2Manifest = Path.Combine(
            Application.dataPath,
            "Content",
            "HotUpdate",
            "release-manifest.json");
        if (!File.Exists(a2Manifest))
            throw new BuildFailedException(
                "Player build requires a staged Addressables release manifest; local runtime fallback is disabled.");
        HotUpdateArtifactStager.Verify();
    }
}
