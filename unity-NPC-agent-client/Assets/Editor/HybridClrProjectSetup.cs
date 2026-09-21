using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
        Configure();
        // H2 produces a local Development/QA player so its staged tool pack may
        // include portable PDB symbols. Release builds omit them in the build hook.
        EditorUserBuildSettings.development = true;
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
        StageLocalArtifacts(EditorUserBuildSettings.development);
    }

    public static void GenerateAndStageFromCommandLine() => RunCommand(GenerateAndStage);

    public static void BuildSmokePlayerFromCommandLine() =>
        BuildSmokePlayerFromCommandLine(
            "HybridClrH2",
            "h2-windows-il2cpp-build.json",
            "H2");

    public static void BuildH3SmokePlayerFromCommandLine() =>
        BuildSmokePlayerFromCommandLine(
            "HybridClrH3",
            "h3-windows-il2cpp-build.json",
            "H3");

    private static void BuildSmokePlayerFromCommandLine(
        string buildDirectoryName,
        string reportFileName,
        string phase)
    {
        RunCommand(() =>
        {
            Configure();
            string outputDirectory = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                "Builds",
                buildDirectoryName);
            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(outputDirectory, "GameWithLLM.exe");
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    $"{phase} Windows IL2CPP build failed: {report.summary.result}.");
            }

            string repository = Directory.GetParent(
                Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath)?.FullName
                ?? Application.dataPath;
            string reportPath = Path.Combine(repository, "Docs", "Baselines", reportFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? repository);
            File.WriteAllText(reportPath, JsonConvert.SerializeObject(new
            {
                unityVersion = Application.unityVersion,
                hybridClrVersion = HybridCLRSettings.Instance != null ? "8.14.1" : "unknown",
                target = report.summary.platform.ToString(),
                scriptingBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone).ToString(),
                apiCompatibility = PlayerSettings.GetApiCompatibilityLevel(NamedBuildTarget.Standalone).ToString(),
                development = true,
                totalBytes = report.summary.totalSize,
                buildSeconds = report.summary.totalTime.TotalSeconds,
                outputPath
            }, Formatting.Indented) + Environment.NewLine);
            Debug.Log($"[Hot Update] {phase} Windows IL2CPP Player built at '{outputPath}'.");
        });
    }

    public static void StageLocalArtifacts(bool includeDebugSymbols)
    {
        BuildTarget target = BuildTarget.StandaloneWindows64;
        string hotUpdateSource = SettingsUtil.GetHotUpdateDllsOutputDirByTarget(target);
        string metadataSource = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
        string destination = Path.Combine(Application.streamingAssetsPath, "HotUpdate");
        Directory.CreateDirectory(destination);

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

        string manifest = JsonConvert.SerializeObject(new
        {
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
            }
        }, Formatting.Indented);
        File.WriteAllText(
            Path.Combine(destination, "local-hot-update-manifest.json"),
            manifest + Environment.NewLine);
        AssetDatabase.Refresh();
        Debug.Log($"[Hot Update] Staged local HybridCLR artifacts at '{destination}'.");
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
        HybridClrProjectSetup.StageLocalArtifacts(
            (report.summary.options & BuildOptions.Development) != 0);
    }
}
