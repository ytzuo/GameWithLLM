using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ContentReleasePipeline
{
    private const string PolicyPath = "Assets/Content/HotUpdate/content-release-policy.json";
    private const string ReleaseManifestPath = "Assets/Content/HotUpdate/release-manifest.json";
    private const string ProfileName = "Production";
    private const string BuildTargetName = "StandaloneWindows64";
    private const string ContentStateName = "addressables_content_state.bin";

    [MenuItem("GameWithLLM/Content/Validate Release Candidate")]
    public static void VerifyProductionGate()
    {
        ContentValidationReport report = ContentValidationRunner.Run(ContentValidationProfile.Candidate);
        ContentValidationRunner.ThrowIfFailed(report);
        Debug.Log("[Release] CANDIDATE_GATE_SUCCESS: release inputs and compatibility are valid.");
    }

    public static void ValidateReleaseInputs()
    {
        AddressableAssetSettings settings = Settings();
        VerifyProfile(settings);
        VerifyEntries(settings);
        VerifyJsonAndBusinessIds();
        VerifyPlayerAndAddressableSeparation(settings);
        VerifyMissingScripts(settings);
        VerifyAotIdentity();
        ReadPolicy();
    }

    [MenuItem("GameWithLLM/Content/Build Full Release Candidate")]
    public static void BuildFullReleaseCandidate()
    {
        HybridClrProjectSetup.GenerateAndStageForPipeline(false);
        HotUpdateArtifactStager.StageAndConfigure();
        VerifyProductionGate();
        VerifyToolPackageSmokeEvidence();
        AddressableAssetSettings settings = Settings();
        WithProductionProfile(settings, () =>
        {
            AddressableAssetSettings.CleanPlayerContent(settings.ActivePlayerDataBuilder);
            BuildAddressables(() =>
            {
                AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
                return result;
            });
        });
        string player = BuildProductionPlayer();
        WriteCandidate("full", null, player);
        Debug.Log("[Release] FULL_CANDIDATE_READY: immutable full release is ready for environment smoke testing.");
    }

    [MenuItem("GameWithLLM/Content/Build Content Update Candidate")]
    public static void BuildContentUpdateCandidate()
    {
        HybridClrProjectSetup.GenerateAndStageForPipeline(false);
        HotUpdateArtifactStager.StageAndConfigure();
        VerifyProductionGate();
        VerifyToolPackageSmokeEvidence();
        string baseline = Environment.GetEnvironmentVariable("CONTENT_BASELINE_STATE_PATH");
        if (string.IsNullOrWhiteSpace(baseline) || !File.Exists(baseline))
            throw new BuildFailedException("CONTENT_BASELINE_STATE_PATH must name the archived Player baseline content_state.bin.");
        baseline = Path.GetFullPath(baseline);
        ValidateBaseline(baseline);
        AddressableAssetSettings settings = Settings();
        WithProductionProfile(settings, () =>
        {
            List<AddressableAssetEntry> restricted = ContentUpdateScript.GatherModifiedEntries(settings, baseline);
            if (restricted == null)
                throw new BuildFailedException("The baseline content state is invalid or incompatible.");
            if (restricted.Count > 0)
                throw new BuildFailedException("Content Update Restrictions failed for: " +
                    string.Join(", ", restricted.Select(item => item.AssetPath).OrderBy(item => item, StringComparer.Ordinal)));
            BuildAddressables(() => ContentUpdateScript.BuildContentUpdate(settings, baseline));
        });
        WriteCandidate("content-update", baseline, null);
        Debug.Log("[Release] UPDATE_CANDIDATE_READY: delta release is ready for existing-install testing.");
    }

    public static void BuildFullFromCommandLine() => RunCommand(BuildFullReleaseCandidate);
    public static void BuildUpdateFromCommandLine() => RunCommand(BuildContentUpdateCandidate);

    private static void VerifyProfile(AddressableAssetSettings settings)
    {
        string profileId = settings.profileSettings.GetProfileId(ProfileName);
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidDataException("Production Addressables profile is missing.");
        string releaseId = settings.profileSettings.GetValueByName(profileId, "ReleaseId");
        string expected = RequiredString(ReadObject(ReleaseManifestPath), "releaseId");
        if (!string.Equals(releaseId, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Production ReleaseId '{releaseId}' does not match manifest '{expected}'.");
        string remote = settings.profileSettings.GetValueByName(profileId, AddressableAssetSettings.kRemoteLoadPath);
        if (string.IsNullOrWhiteSpace(remote) || !remote.Contains("[ReleaseId]", StringComparison.Ordinal))
            throw new InvalidDataException("Production Remote.LoadPath must be non-empty and release-versioned.");
    }

    private static void VerifyEntries(AddressableAssetSettings settings)
    {
        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (AddressableAssetGroup group in settings.groups.Where(item => item != null))
        {
            foreach (AddressableAssetEntry entry in group.entries)
            {
                if (string.IsNullOrWhiteSpace(entry.address))
                    throw new InvalidDataException($"Addressable entry '{entry.AssetPath}' has an empty address.");
                if (addresses.TryGetValue(entry.address, out string previous))
                    throw new InvalidDataException($"Address '{entry.address}' is shared by '{previous}' and '{entry.AssetPath}'.");
                addresses.Add(entry.address, entry.AssetPath);
            }
        }
        string[] groups = { "Remote_ClientConfig", "Remote_HotfixMetadata", "Remote_ToolPacks", "Remote_UI",
            "Remote_SpritesTextures", "Remote_Materials", "Remote_Characters", "Remote_Scenes" };
        foreach (string group in groups)
            if (settings.FindGroup(group) == null)
                throw new InvalidDataException($"Required content Group '{group}' is missing.");
        string[] labels = { "content.client-config", "content.release-manifest", "content.hotfix-metadata",
            "content.hotfix-tools", "content.ui", "content.items", "content.characters", "content.scenes" };
        foreach (string label in labels)
            if (!settings.GetLabels().Contains(label))
                throw new InvalidDataException($"Required content Label '{label}' is missing.");
    }

    private static void VerifyJsonAndBusinessIds()
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles("Assets/Content", "*.json", SearchOption.AllDirectories))
        {
            JToken root;
            try { root = JToken.Parse(File.ReadAllText(path)); }
            catch (Exception ex) { throw new InvalidDataException($"JSON '{path}' is invalid.", ex); }
            if ((int?)root["schemaVersion"] != 1)
                throw new InvalidDataException($"JSON '{path}' must use schemaVersion 1.");
        }
        JObject characters = ReadObject("Assets/Content/Characters/character-catalog.json");
        foreach (JObject item in characters["appearances"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            AddUnique(ids, "character/appearance", RequiredString(item, "characterId") + "/" + RequiredString(item, "appearanceId"));
        JObject release = ReadObject(ReleaseManifestPath);
        foreach (JObject tool in release["activeTools"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            AddUnique(ids, "tool", RequiredString(tool, "name"));
        var catalogIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JObject catalog in release["catalogs"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            if (!catalogIds.Add(RequiredString(catalog, "catalogId")))
                throw new InvalidDataException("Release manifest contains a duplicate catalogId.");
    }

    private static void VerifyPlayerAndAddressableSeparation(AddressableAssetSettings settings)
    {
        var buildScenes = new HashSet<string>(EditorBuildSettings.scenes.Where(item => item.enabled)
            .Select(item => item.path), StringComparer.Ordinal);
        foreach (AddressableAssetGroup group in settings.groups.Where(item => item != null))
        foreach (AddressableAssetEntry entry in group.entries)
        {
            if (entry.AssetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidDataException($"Addressable '{entry.AssetPath}' is also under Resources.");
            if (buildScenes.Contains(entry.AssetPath))
                throw new InvalidDataException($"Scene '{entry.AssetPath}' is both Addressable and in Build Settings.");
        }
    }

    private static void VerifyMissingScripts(AddressableAssetSettings settings)
    {
        foreach (AddressableAssetGroup group in settings.groups.Where(item => item != null))
        foreach (AddressableAssetEntry entry in group.entries)
        {
            if (entry.AssetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.AssetPath);
                if (prefab != null && CountMissing(prefab) > 0)
                    throw new InvalidDataException($"Prefab '{entry.AssetPath}' contains a Missing Script.");
            }
            else if (entry.AssetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                Scene scene = EditorSceneManager.OpenScene(entry.AssetPath, OpenSceneMode.Single);
                if (scene.GetRootGameObjects().Sum(CountMissing) > 0)
                    throw new InvalidDataException($"Scene '{entry.AssetPath}' contains a Missing Script.");
            }
        }
        foreach (string path in EditorBuildSettings.scenes.Where(item => item.enabled).Select(item => item.path))
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            if (scene.GetRootGameObjects().Sum(CountMissing) > 0)
                throw new InvalidDataException($"Build Settings scene '{path}' contains a Missing Script.");
        }
    }

    private static void VerifyAotIdentity()
    {
        JObject release = ReadObject(ReleaseManifestPath);
        string playerBuildId = RequiredString(release, "playerBuildId");
        foreach (JObject metadata in release["aotMetadata"]?.Children<JObject>() ?? Enumerable.Empty<JObject>())
            if (!RequiredString(metadata, "address").StartsWith("hotfix/aot/" + playerBuildId + "/", StringComparison.Ordinal))
                throw new InvalidDataException("AOT metadata does not match the target Player build.");
    }

    private static void ValidateBaseline(string contentStatePath)
    {
        string manifestPath = Path.Combine(Path.GetDirectoryName(contentStatePath) ?? string.Empty,
            "candidate-manifest.json");
        JObject baseline = ReadObject(manifestPath);
        JObject release = ReadObject(ReleaseManifestPath);
        if ((int?)baseline["schemaVersion"] != 1 ||
            !string.Equals((string)baseline["releaseKind"], "full", StringComparison.Ordinal) ||
            !string.Equals((string)baseline["unityVersion"], Application.unityVersion, StringComparison.Ordinal) ||
            !string.Equals((string)baseline["addressablesVersion"], "2.9.1", StringComparison.Ordinal) ||
            !string.Equals((string)baseline["playerBuildId"], (string)release["playerBuildId"], StringComparison.Ordinal) ||
            !string.Equals((string)baseline["contentStateSha256"], ArtifactHash.Sha256File(contentStatePath),
                StringComparison.OrdinalIgnoreCase))
            throw new BuildFailedException("Content state does not match the Unity/Addressables/Player baseline.");
        if (string.Equals((string)baseline["releaseId"], (string)release["releaseId"], StringComparison.Ordinal))
            throw new BuildFailedException("Content update must use a new immutable releaseId.");
    }

    private static void VerifyToolPackageSmokeEvidence()
    {
        JObject release = ReadObject(ReleaseManifestPath);
        string path = Path.Combine(
            RepositoryRoot(), "Artifacts", "Content", RequiredString(release, "releaseId"),
            "tool-package-smoke.passed.json");
        JObject evidence = ReadObject(path);
        if ((int?)evidence["schemaVersion"] != 1 ||
            !string.Equals((string)evidence["releaseId"], (string)release["releaseId"], StringComparison.Ordinal) ||
            !string.Equals((string)evidence["toolSetVersion"], (string)release["toolSetVersion"], StringComparison.Ordinal) ||
            !string.Equals((string)evidence["manifestSha256"], ArtifactHash.Sha256File(ReleaseManifestPath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((string)evidence["successMarker"], "TOOL_PACKAGE_SMOKE_SUCCESS", StringComparison.Ordinal))
            throw new BuildFailedException("Tool package Player smoke evidence is missing, stale, or invalid.");
    }

    private static int CountMissing(GameObject root) =>
        root.GetComponentsInChildren<Transform>(true).Sum(item =>
            GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(item.gameObject));

    private static void BuildAddressables(Func<AddressablesPlayerBuildResult> build)
    {
        UnityEditor.AddressableAssets.Settings.ProjectConfigData.GenerateBuildLayout = true;
        UnityEditor.AddressableAssets.Settings.ProjectConfigData.BuildLayoutReportFileFormat =
            UnityEditor.AddressableAssets.Settings.ProjectConfigData.ReportFileFormat.JSON;
        AddressablesPlayerBuildResult result = build();
        if (result == null || !string.IsNullOrWhiteSpace(result.Error))
            throw new BuildFailedException("Release Addressables build failed: " + (result?.Error ?? "no result"));
        List<AnalyzeRule.AnalyzeResult> analysis = new CheckBundleDupeDependencies().RefreshAnalysis(Settings());
        string[] issues = analysis.Where(item => item.severity == MessageType.Warning || item.severity == MessageType.Error)
            .Select(item => item.resultName).Where(item => !string.IsNullOrWhiteSpace(item)).ToArray();
        if (issues.Length > 0)
            throw new BuildFailedException("Release duplicate dependency Analyze failed: " + string.Join("; ", issues));
    }

    private static string BuildProductionPlayer()
    {
        VerifyProductionPlayerStagingIsClean();
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "ContentReleaseProduction", "GameWithLLM.exe"));
        AddressableAssetSettings settings = Settings();
        return WindowsPlayerBuilder.Build(settings, ProfileName, output,
            EditorBuildSettings.scenes.Where(item => item.enabled).Select(item => item.path),
            BuildOptions.CleanBuildCache);
    }

    private static void VerifyProductionPlayerStagingIsClean()
    {
        foreach (string path in new[]
                 {
                     "Assets/StreamingAssets/HotUpdate",
                     "Assets/StreamingAssets/SmokeTests"
                 })
        {
            if (Directory.Exists(path) || File.Exists(path))
                throw new BuildFailedException(
                    $"Production Player cannot contain local hot-update or smoke staging: '{path}'.");
        }
    }

    private static void WriteCandidate(string kind, string baseline, string player)
    {
        JObject release = ReadObject(ReleaseManifestPath);
        JObject policy = ReadPolicy();
        string releaseId = RequiredString(release, "releaseId");
        string root = RepositoryRoot();
        string candidateDirectory = Path.Combine(root, "Artifacts", "Content", releaseId);
        Directory.CreateDirectory(candidateDirectory);
        string toolPackageEvidence = Path.Combine(candidateDirectory, "tool-package-smoke.passed.json");
        if (!File.Exists(toolPackageEvidence))
            throw new FileNotFoundException("Tool package smoke evidence is missing.", toolPackageEvidence);
        string serverDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ServerData", "production", BuildTargetName));
        if (!Directory.Exists(serverDirectory))
            throw new DirectoryNotFoundException("Production Addressables output is missing: " + serverDirectory);
        string releaseDirectory = Path.Combine(serverDirectory, releaseId);
        string catalogDirectory = Path.Combine(serverDirectory, "catalog");
        if (!Directory.Exists(releaseDirectory) || !Directory.Exists(catalogDirectory))
            throw new DirectoryNotFoundException("Versioned bundles or the production catalog are missing.");

        string state = FindContentState();
        string archivedState = Path.Combine(candidateDirectory, ContentStateName);
        File.Copy(state, archivedState, true);
        var roots = new List<string> { releaseDirectory, catalogDirectory };
        if (!string.IsNullOrWhiteSpace(player)) roots.Add(Path.GetDirectoryName(player));
        string[] candidateContent = roots.Take(2).SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories)).ToArray();
        long remoteBytes = candidateContent.Sum(path => new FileInfo(path).Length);
        string[] bundles = Directory.GetFiles(releaseDirectory, "*.bundle", SearchOption.AllDirectories);
        long largestBundle = bundles.Length == 0 ? 0 : bundles.Max(path => new FileInfo(path).Length);
        long estimatedPeakMemory = checked(largestBundle * 2);
        int duplicateImplicitAssets = ReadDuplicateImplicitAssetCount();
        JObject budgets = (JObject)policy["budgets"];
        EnforceBudget("bundle count", bundles.LongLength, (long)budgets["maximumBundleCount"]);
        EnforceBudget("remote bytes", remoteBytes, (long)budgets["maximumTotalRemoteBytes"]);
        EnforceBudget("largest bundle bytes", largestBundle, (long)budgets["maximumLargestBundleBytes"]);
        EnforceBudget("estimated peak memory bytes", estimatedPeakMemory,
            (long)budgets["maximumEstimatedPeakMemoryBytes"]);
        EnforceBudget("duplicate implicit assets", duplicateImplicitAssets,
            (long)budgets["maximumDuplicateImplicitAssets"]);
        long patchBytes = string.Equals(kind, "content-update", StringComparison.Ordinal) ? remoteBytes : 0;
        if (patchBytes > 0) EnforceBudget("patch bytes", patchBytes, (long)budgets["maximumPatchBytes"]);

        JObject manifest = new JObject
        {
            ["schemaVersion"] = 1,
            ["releaseId"] = releaseId,
            ["releaseKind"] = kind,
            ["toolSetVersion"] = release["toolSetVersion"],
            ["playerBuildId"] = release["playerBuildId"],
            ["unityVersion"] = Application.unityVersion,
            ["addressablesVersion"] = "2.9.1",
            ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
            ["toolPackageSmokeEvidence"] = "tool-package-smoke.passed.json",
            ["toolPackageSmokeEvidenceSha256"] = ArtifactHash.Sha256File(toolPackageEvidence),
            ["baselineContentStateSha256"] = string.IsNullOrWhiteSpace(baseline) ? null : ArtifactHash.Sha256File(baseline),
            ["contentStateSha256"] = ArtifactHash.Sha256File(archivedState),
            ["bundleCount"] = bundles.Length,
            ["remoteBytes"] = remoteBytes,
            ["largestBundleBytes"] = largestBundle,
            ["estimatedPeakMemoryBytes"] = estimatedPeakMemory,
            ["duplicateImplicitAssets"] = duplicateImplicitAssets,
            ["files"] = ArtifactFileManifest.Create(root, roots, new[] { archivedState })
        };
        File.WriteAllText(Path.Combine(candidateDirectory, "candidate-manifest.json"),
            manifest.ToString(Formatting.Indented) + Environment.NewLine);
    }

    private static string FindContentState()
    {
        string configured = ContentUpdateScript.GetContentStateDataPath(false, Settings());
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        string[] candidates = Directory.GetFiles(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
            ContentStateName, SearchOption.AllDirectories)
            .Where(path => path.IndexOf(Path.DirectorySeparatorChar + "Library" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) < 0)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        if (candidates.Length == 0)
            throw new FileNotFoundException("Addressables content_state.bin was not generated.");
        return candidates[0];
    }

    private static int ReadDuplicateImplicitAssetCount()
    {
        string layout = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library",
            "com.unity.addressables", "buildlayout.json"));
        if (!File.Exists(layout))
            throw new FileNotFoundException("Release candidate requires the Addressables JSON Build Layout report.", layout);
        JToken root = JToken.Parse(File.ReadAllText(layout));
        return root.SelectTokens("$..DuplicatedAssets")
            .OfType<JArray>()
            .SelectMany(array => array.Children())
            .Count();
    }

    private static void EnforceBudget(string name, long actual, long maximum)
    {
        if (actual > maximum) throw new BuildFailedException($"Release {name} budget exceeded: {actual} > {maximum}.");
    }

    private static JObject ReadPolicy()
    {
        JObject policy = ReadObject(PolicyPath);
        if ((int?)policy["schemaVersion"] != 1 || policy["budgets"] is not JObject ||
            (int?)policy["retentionDays"] < 1 || (int?)policy["minimumRetainedReleases"] < 2)
            throw new InvalidDataException("Content release policy is invalid.");
        return policy;
    }

    private static void AddUnique(IDictionary<string, string> ids, string kind, string value)
    {
        string key = kind + ":" + value;
        if (ids.ContainsKey(key)) throw new InvalidDataException($"Duplicate {kind} business ID '{value}'.");
        ids.Add(key, value);
    }

    private static AddressableAssetSettings Settings() => AddressableAssetSettingsDefaultObject.Settings ??
        throw new InvalidOperationException("Addressables settings are missing.");

    private static void WithProductionProfile(AddressableAssetSettings settings, Action action)
    {
        using (new AddressablesProfileScope(settings, ProfileName))
            action();
    }

    private static JObject ReadObject(string path) => File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) :
        throw new FileNotFoundException("Required content release file is missing: " + path, path);

    private static string RequiredString(JObject value, string property)
    {
        string result = (string)value?[property];
        return !string.IsNullOrWhiteSpace(result) ? result :
            throw new InvalidDataException($"Required content release field '{property}' is missing.");
    }

    private static string RepositoryRoot() => new ContentBuildContext().RepositoryRoot;

    private static void RunCommand(Action action)
    {
        HotUpdateEditorCommand.Run(action);
    }
}
