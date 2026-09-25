using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using GameWithLLM.AgentRuntime;
using HybridCLR.Editor.Settings;
using Mono.Cecil;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class HybridClrH7ReleasePipeline
{
    private const string PolicyPath = "Assets/Content/HotUpdate/h7-release-policy.json";
    private const string ManifestPath = "Assets/Content/HotUpdate/release-manifest.json";
    private const string SmokePlanPath = "Assets/StreamingAssets/H7/h7-smoke-plan.json";
    private const string ToolMetadataPath = "Assets/Content/Catalogs/tool_metadata.zh-CN.json";
    private const string ExpectedUnityVersion = "6000.3.19f1";
    private const string ExpectedHybridClrVersion = "8.14.1";

    [MenuItem("GameWithLLM/Hot Update/H7/Prepare Candidate And Smoke Player")]
    public static void PrepareCandidateAndSmokePlayer()
    {
        HybridClrProjectSetup.GenerateAndStageForPipeline(false);
        AddressablesA2ProjectSetup.StageAndConfigure();
        VerifyProductionGate();
        BuildAddressables("LocalDevelopment");
        BuildPlayer("HybridClrH7Smoke", BuildOptions.Development | BuildOptions.CleanBuildCache,
            "LocalDevelopment");
        Debug.Log("[Hot Update] H7_CANDIDATE_READY: production gate passed and smoke Player was built.");
    }

    [MenuItem("GameWithLLM/Hot Update/H7/Finalize Production Candidate")]
    public static void FinalizeProductionCandidate()
    {
        JObject manifest = VerifyProductionGate();
        VerifySmokeEvidence(manifest);
        BuildAddressables("Production");
        string playerPath = BuildPlayer("HybridClrH7Production", BuildOptions.CleanBuildCache, "Production");
        WriteArtifactManifest(manifest, playerPath);
        Debug.Log("[Hot Update] H7_PRODUCTION_CANDIDATE_READY: immutable artifacts and hashes are ready for promotion.");
    }

    [MenuItem("GameWithLLM/Hot Update/H7/Verify Production Gate")]
    public static JObject VerifyProductionGate()
    {
        VerifyPinnedToolchain();
        AddressablesA2ProjectSetup.Verify();
        JObject policy = ReadObject(PolicyPath);
        JObject manifest = ReadObject(ManifestPath);
        JObject smokePlan = ReadObject(SmokePlanPath);
        VerifyEnvelope(policy, manifest, smokePlan);
        VerifyAssemblyReferences(policy, manifest);
        VerifyToolSetAndCatalog(manifest);
        VerifySmokeCoverage(manifest, smokePlan, policy);
        WriteSchemaSnapshot(manifest);
        Debug.Log($"[Hot Update] H7_PRODUCTION_GATE_SUCCESS: release '{(string)manifest["releaseId"]}'.");
        return manifest;
    }

    public static void PrepareCandidateAndSmokePlayerFromCommandLine() =>
        RunCommand(PrepareCandidateAndSmokePlayer);

    public static void FinalizeProductionCandidateFromCommandLine() =>
        RunCommand(FinalizeProductionCandidate);

    public static void VerifyProductionGateFromCommandLine() =>
        RunCommand(() => VerifyProductionGate());

    private static void VerifyPinnedToolchain()
    {
        if (!string.Equals(Application.unityVersion, ExpectedUnityVersion, StringComparison.Ordinal))
            throw new BuildFailedException(
                $"H7 requires Unity {ExpectedUnityVersion}; current Editor is {Application.unityVersion}.");
        string manifest = File.ReadAllText("Packages/manifest.json");
        if (!manifest.Contains("hybridclr_unity.git#v" + ExpectedHybridClrVersion))
            throw new BuildFailedException($"H7 requires HybridCLR {ExpectedHybridClrVersion}.");
        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone) != ScriptingImplementation.IL2CPP ||
            PlayerSettings.GetArchitecture(BuildTargetGroup.Standalone) != 1)
            throw new BuildFailedException("H7 requires Windows x86_64 IL2CPP Player settings.");
    }

    private static void VerifyEnvelope(JObject policy, JObject manifest, JObject smokePlan)
    {
        if ((int?)policy["schemaVersion"] != 1 || (int?)manifest["schemaVersion"] != 1 ||
            (int?)smokePlan["schemaVersion"] != 1)
            throw new InvalidDataException("H7 policy, manifest and smoke plan must use schemaVersion 1.");
        string releaseId = RequiredString(manifest, "releaseId");
        string toolSetVersion = RequiredString(manifest, "toolSetVersion");
        if (!string.Equals(releaseId, (string)smokePlan["releaseId"], StringComparison.Ordinal) ||
            !string.Equals(toolSetVersion, (string)smokePlan["toolSetVersion"], StringComparison.Ordinal))
            throw new InvalidDataException("H7 smoke plan does not target the staged release and ToolSet.");
        string playerBuildId = RequiredString(manifest, "playerBuildId");
        foreach (JObject metadata in manifest["aotMetadata"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string address = RequiredString(metadata, "address");
            if (!address.StartsWith("hotfix/aot/" + playerBuildId + "/", StringComparison.Ordinal))
                throw new InvalidDataException($"AOT metadata '{address}' does not match Player build '{playerBuildId}'.");
        }
        string expectedRelease = Environment.GetEnvironmentVariable("H7_RELEASE_ID");
        if (!string.IsNullOrWhiteSpace(expectedRelease) &&
            !string.Equals(expectedRelease, releaseId, StringComparison.Ordinal))
            throw new InvalidDataException($"H7_RELEASE_ID '{expectedRelease}' does not match '{releaseId}'.");
    }

    private static void VerifyAssemblyReferences(JObject policy, JObject manifest)
    {
        var allowed = new HashSet<string>(
            policy["allowedAssemblyReferences"]?.Values<string>() ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        string[] allowedPrefixes = policy["allowedFrameworkReferencePrefixes"]?.Values<string>().ToArray()
                                   ?? Array.Empty<string>();
        foreach (JObject package in manifest["toolPackages"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
        {
            string assemblyName = RequiredString(package, "assemblyName");
            string fileName = RequiredString((JObject)package["assembly"], "fileName");
            string path = "Assets/Content/HotUpdate/ToolPacks/" + fileName;
            if (!File.Exists(path))
                throw new FileNotFoundException($"H7 tool package '{assemblyName}' is missing.", path);
            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path);
            if (!string.Equals(assembly.Name.Name, assemblyName, StringComparison.Ordinal))
                throw new InvalidDataException($"Tool package file declares '{assembly.Name.Name}', not '{assemblyName}'.");
            string[] denied = assembly.MainModule.AssemblyReferences
                .Select(reference => reference.Name)
                .Where(reference => !allowed.Contains(reference) &&
                                    !allowedPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
                .OrderBy(reference => reference, StringComparer.Ordinal)
                .ToArray();
            if (denied.Length > 0)
                throw new InvalidDataException(
                    $"Tool package '{assemblyName}' has forbidden references: {string.Join(", ", denied)}.");
        }
    }

    private static void VerifyToolSetAndCatalog(JObject manifest)
    {
        HotUpdateReleaseManifest release = manifest.ToObject<HotUpdateReleaseManifest>() ??
                                           throw new InvalidDataException("H7 release manifest is invalid.");
        var tools = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        foreach (IAgentTool tool in AgentToolDiscovery.DiscoverBuiltinTools())
            tools.Add(tool.Descriptor.Name, tool);
        foreach (HotUpdateToolPackageDeclaration package in release.toolPackages)
        {
            System.Reflection.Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(candidate =>
                string.Equals(candidate.GetName().Name, package.assemblyName, StringComparison.Ordinal)) ??
                throw new InvalidOperationException($"Editor assembly '{package.assemblyName}' is not loaded.");
            foreach (IAgentTool tool in AgentToolDiscovery.DiscoverFromAssembly(assembly))
                tools.Add(tool.Descriptor.Name, tool);
        }
        ToolCandidate[] candidates = release.activeTools.Select(declaration =>
            new ToolCandidate(
                declaration,
                tools.TryGetValue(declaration.name, out IAgentTool tool)
                    ? tool
                    : throw new InvalidDataException($"Release tool '{declaration.name}' has no discovered implementation.")))
            .ToArray();
        var candidate = new ToolSetCandidate(
            release.releaseId,
            release.toolSetVersion,
            release.catalogVersion,
            candidates,
            release.retiredTools,
            release.toolHistory);
        ToolSetSnapshot snapshot = ToolSetValidator.Prepare(candidate, null);
        ToolMetadataCatalog.ParseAndValidate(File.ReadAllText(ToolMetadataPath), snapshot, release.catalogVersion);
    }

    private static void VerifySmokeCoverage(JObject manifest, JObject smokePlan, JObject policy)
    {
        var calls = (smokePlan["smokeCalls"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            .ToDictionary(item => RequiredString(item, "toolName"), StringComparer.Ordinal);
        var active = manifest["activeTools"]?.Children<JObject>().ToArray() ?? Array.Empty<JObject>();
        foreach (JObject tool in active.Where(item => string.Equals((string)item["source"], "hot-update", StringComparison.Ordinal)))
        {
            string name = RequiredString(tool, "name");
            if (!calls.ContainsKey(name))
                throw new InvalidDataException($"Hot-update tool '{name}' has no H7 smoke call.");
        }
        JObject approved = policy["approvedTools"] as JObject ?? new JObject();
        foreach (JObject tool in active)
        {
            string name = RequiredString(tool, "name");
            string fingerprint = ToolFingerprint(tool);
            if ((!approved.TryGetValue(name, out JToken prior) ||
                 !string.Equals((string)prior, fingerprint, StringComparison.OrdinalIgnoreCase)) &&
                !calls.ContainsKey(name))
                throw new InvalidDataException($"New or modified tool '{name}' has no H7 smoke call.");
        }
        var retired = new HashSet<string>(
            manifest["retiredTools"]?.Children<JObject>().Select(item => RequiredString(item, "name"))
            ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        var smokeRetired = new HashSet<string>(
            smokePlan["retiredTools"]?.Values<string>() ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
        if (!retired.SetEquals(smokeRetired))
            throw new InvalidDataException("H7 smoke plan must verify every and only release tombstone.");
        foreach (JObject call in calls.Values)
        {
            string name = RequiredString(call, "toolName");
            if (!active.Any(item => string.Equals((string)item["name"], name, StringComparison.Ordinal)))
                throw new InvalidDataException($"H7 smoke call targets inactive tool '{name}'.");
            if (!(call["arguments"] is JObject))
                throw new InvalidDataException($"H7 smoke call '{name}' arguments must be a JSON object.");
        }
    }

    private static string ToolFingerprint(JObject tool) => Sha256(
        string.Join("|", new[]
        {
            (string)tool["toolIdentity"], (string)tool["source"],
            (string)tool["implementationVersion"], (string)tool["contractVersion"],
            (string)tool["packageId"], (string)tool["packageVersion"],
            (string)tool["assemblyName"], (string)tool["assemblyHash"], (string)tool["schemaHash"]
        }));

    private static void WriteSchemaSnapshot(JObject manifest)
    {
        string root = RepositoryRoot();
        string outputDirectory = Path.Combine(root, "Artifacts", "H7", (string)manifest["releaseId"]);
        Directory.CreateDirectory(outputDirectory);
        var snapshot = new JObject
        {
            ["schemaVersion"] = 1,
            ["releaseId"] = manifest["releaseId"],
            ["toolSetVersion"] = manifest["toolSetVersion"],
            ["tools"] = new JArray((manifest["activeTools"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
                .OrderBy(item => (string)item["name"], StringComparer.Ordinal)
                .Select(item => new JObject
                {
                    ["name"] = item["name"],
                    ["contractVersion"] = item["contractVersion"],
                    ["schemaHash"] = item["schemaHash"]
                }))
        };
        File.WriteAllText(Path.Combine(outputDirectory, "tool-schema-snapshot.json"),
            snapshot.ToString(Formatting.Indented) + Environment.NewLine);
    }

    private static void BuildAddressables(string profileName)
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        string profileId = settings.profileSettings.GetProfileId(profileName);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"Addressables profile '{profileName}' is missing.");
        string original = settings.activeProfileId;
        try
        {
            settings.activeProfileId = profileId;
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new BuildFailedException($"H7 Addressables '{profileName}' build failed: {result.Error}");
        }
        finally
        {
            settings.activeProfileId = original;
            AssetDatabase.SaveAssets();
        }
    }

    private static string BuildPlayer(string directoryName, BuildOptions options, string profileName)
    {
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", directoryName, "GameWithLLM.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? throw new InvalidOperationException("Build path is invalid."));
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        string original = settings.activeProfileId;
        string profileId = settings.profileSettings.GetProfileId(profileName);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException($"Addressables profile '{profileName}' is missing.");
        BuildReport report;
        try
        {
            settings.activeProfileId = profileId;
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = options
            });
        }
        finally
        {
            settings.activeProfileId = original;
            AssetDatabase.SaveAssets();
        }
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"H7 Windows IL2CPP Player build failed: {report.summary.result}.");
        return output;
    }

    private static void WriteArtifactManifest(JObject release, string playerPath)
    {
        string root = RepositoryRoot();
        string releaseId = (string)release["releaseId"];
        string outputDirectory = Path.Combine(root, "Artifacts", "H7", releaseId);
        Directory.CreateDirectory(outputDirectory);
        var roots = new[]
        {
            Path.GetDirectoryName(playerPath),
            Path.Combine(Application.dataPath, "..", "ServerData", "production", "StandaloneWindows64")
        };
        var files = roots.Where(Directory.Exists).SelectMany(directory =>
                Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new JObject
            {
                ["path"] = Path.GetRelativePath(root, path).Replace('\\', '/'),
                ["length"] = new FileInfo(path).Length,
                ["sha256"] = Sha256(File.ReadAllBytes(path))
            });
        var attestation = new JObject
        {
            ["schemaVersion"] = 1,
            ["releaseId"] = releaseId,
            ["toolSetVersion"] = release["toolSetVersion"],
            ["playerBuildId"] = release["playerBuildId"],
            ["unityVersion"] = Application.unityVersion,
            ["hybridClrVersion"] = ExpectedHybridClrVersion,
            ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA") ??
                           Environment.GetEnvironmentVariable("CI_COMMIT_SHA") ?? "local",
            ["smokeEvidence"] = "h7-player-smoke.passed.json",
            ["files"] = new JArray(files)
        };
        File.WriteAllText(Path.Combine(outputDirectory, "candidate-manifest.json"),
            attestation.ToString(Formatting.Indented) + Environment.NewLine);
    }

    private static void VerifySmokeEvidence(JObject release)
    {
        string path = Path.Combine(
            RepositoryRoot(), "Artifacts", "H7", (string)release["releaseId"],
            "h7-player-smoke.passed.json");
        JObject evidence = ReadObject(path);
        if (!string.Equals((string)evidence["releaseId"], (string)release["releaseId"], StringComparison.Ordinal) ||
            !string.Equals((string)evidence["toolSetVersion"], (string)release["toolSetVersion"], StringComparison.Ordinal) ||
            !string.Equals((string)evidence["manifestSha256"], Sha256(File.ReadAllBytes(ManifestPath)),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((string)evidence["successMarker"], "H7_PLAYER_SMOKE_SUCCESS", StringComparison.Ordinal))
            throw new BuildFailedException("H7 Player smoke evidence is missing, stale, or invalid.");
    }

    private static JObject ReadObject(string path) => File.Exists(path)
        ? JObject.Parse(File.ReadAllText(path))
        : throw new FileNotFoundException($"Required H7 file is missing: '{path}'.", path);

    private static string RequiredString(JObject value, string property)
    {
        string result = (string)value?[property];
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException($"Required H7 field '{property}' is missing.");
        return result;
    }

    private static string RepositoryRoot() => Directory.GetParent(
        Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath)?.FullName
        ?? throw new InvalidOperationException("Repository root is unavailable.");

    private static string Sha256(string value) => Sha256(System.Text.Encoding.UTF8.GetBytes(value));

    private static string Sha256(byte[] bytes)
    {
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void RunCommand(Action action)
    {
        try { action(); EditorApplication.Exit(0); }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
}
