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

    [MenuItem("GameWithLLM/Hot Update/Configure HybridCLR")]
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
            // H2 stages DLLs directly in StreamingAssets. Addressables integration
            // starts at H6 and must not run during HybridCLR's temporary AOT build.
            addressableSettings.BuildAddressablesWithPlayerBuild =
                AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            EditorUtility.SetDirty(addressableSettings);
        }
        AssetDatabase.SaveAssets();
        Debug.Log("[Hot Update] HybridCLR configured for Windows x86_64 IL2CPP and SmokeTest.");
    }

    public static void InstallFromCommandLine()
    {
        RunCommand(() =>
        {
            Configure();
            var installer = new InstallerController();
            if (!installer.HasInstalledHybridCLR() ||
                !string.Equals(installer.InstalledLibil2cppVersion, installer.PackageVersion, StringComparison.Ordinal))
            {
                installer.InstallDefaultHybridCLR();
            }
            if (!installer.HasInstalledHybridCLR())
                throw new BuildFailedException("HybridCLR Installer did not produce a local il2cpp runtime.");
            Debug.Log($"[Hot Update] HybridCLR Installer ready: {installer.PackageVersion}.");
        });
    }

    [MenuItem("GameWithLLM/Hot Update/Generate HybridCLR And Stage Local Artifacts")]
    public static void GenerateAndStage()
    {
        GenerateAndStageForPipeline(true);
    }

    public static void GenerateAndStageForPipeline(bool includeDebugSymbols)
    {
        Configure();
        // H2 produces a local Development/QA player with portable PDB symbols.
        // H7 invokes the same generator with includeDebugSymbols=false so the
        // production candidate never stages or publishes symbols.
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

    public static void GenerateAndStageFromCommandLine() => RunCommand(GenerateAndStage);

    public static void StageLocalArtifactsFromCommandLine() =>
        RunCommand(() => StageLocalArtifacts(EditorUserBuildSettings.development));

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
                throw new FileNotFoundException($"Required H5 Catalog is missing: '{catalogFile}'.", source);
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
                packageId = hot ? HybridClrBootstrap.SmokePackageId : null,
                packageVersion = hot ? HybridClrBootstrap.SmokePackageVersion : null,
                assemblyName = tool.GetType().Assembly.GetName().Name,
                assemblyHash = hot ? hotDllHash : null,
                schemaHash = ToolSetValidator.ComputeSchemaHash(tool.Descriptor.InputSchemaJson)
            };
        }).OrderBy(tool => tool.name, StringComparer.Ordinal).ToArray();
        string repositoryRoot = Directory.GetParent(
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath)?.FullName
            ?? Application.dataPath;
        ToolHistoryDeclaration[] history = LoadAndValidateHistoryLedger(
            Path.Combine(repositoryRoot, "Docs", "Baselines", "h4-tool-history.json"),
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
                    packageId = HybridClrBootstrap.SmokePackageId,
                    packageVersion = HybridClrBootstrap.SmokePackageVersion,
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
            throw new FileNotFoundException("The committed H4 tool history ledger is missing.", path);
        ToolHistoryDeclaration[] history =
            JsonConvert.DeserializeObject<ToolHistoryDeclaration[]>(File.ReadAllText(path))
            ?? throw new InvalidDataException("The H4 tool history ledger is invalid.");
        var active = activeTools.ToDictionary(tool => tool.name, StringComparer.Ordinal);
        var retired = retiredTools.ToDictionary(tool => tool.name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ToolHistoryDeclaration record in history)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.name) || !seen.Add(record.name))
                throw new InvalidDataException("The H4 history ledger contains a null or duplicate tool record.");
            if (active.TryGetValue(record.name, out ToolReleaseDeclaration declaration))
            {
                if (!string.Equals(record.toolIdentity, declaration.toolIdentity, StringComparison.Ordinal) ||
                    !string.Equals(record.source, declaration.source, StringComparison.Ordinal) ||
                    !string.Equals(record.implementationVersion, declaration.implementationVersion, StringComparison.Ordinal) ||
                    !string.Equals(record.contractVersion, declaration.contractVersion, StringComparison.Ordinal) ||
                    !string.Equals(record.schemaHash, declaration.schemaHash, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(record.assemblyHash ?? string.Empty, declaration.assemblyHash ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(record.retiredInToolSetVersion))
                    throw new InvalidDataException($"H4 history ledger does not match active tool '{record.name}'.");
            }
            else if (retired.TryGetValue(record.name, out RetiredToolDeclaration tombstone))
            {
                if (!string.Equals(record.toolIdentity, tombstone.toolIdentity, StringComparison.Ordinal) ||
                    !string.Equals(record.retiredInToolSetVersion, tombstone.retiredInToolSetVersion, StringComparison.Ordinal))
                    throw new InvalidDataException($"H4 history ledger does not match retired tool '{record.name}'.");
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
                throw new InvalidDataException($"Release tool '{name}' is missing from the committed H4 history ledger.");
        }
        return history;
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using (SHA256 sha256 = SHA256.Create())
            return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void RunCommand(Action action)
    {
        try
        {
            action();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
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
        if (File.Exists(a2Manifest))
        {
            // A2 已把交付物迁入 Addressables；不要再次把同一份 DLL/metadata
            // 写入 StreamingAssets 形成双来源。
            AddressablesA2ProjectSetup.Verify();
            return;
        }
        HybridClrProjectSetup.StageLocalArtifacts(
            (report.summary.options & BuildOptions.Development) != 0);
    }
}
