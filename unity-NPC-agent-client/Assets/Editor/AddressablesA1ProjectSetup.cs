using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AddressablesA1ProjectSetup
{
    private const string ReleaseIdVariable = "ReleaseId";
    private const string RemoteCatalogBuildPathVariable = "RemoteCatalog.BuildPath";
    private const string RemoteCatalogLoadPathVariable = "RemoteCatalog.LoadPath";
    private const string BootstrapProbePath =
        "Assets/Content/Bootstrap/a1-bootstrap-probe.json";
    private const string BootstrapProbeAddress = "config/bootstrap/a1-probe";
    private const string BootstrapRequiredLabel = "content.a1-required";

    private static readonly string[] LocalGroups =
    {
        "Local_Bootstrap",
        "Local_SampleScene",
        "Local_UnityPackage"
    };

    private static readonly string[] RemoteGroups =
    {
        "Remote_ClientConfig",
        "Remote_HotfixMetadata",
        "Remote_ToolPacks",
        "Remote_UI",
        "Remote_SpritesTextures",
        "Remote_Materials",
        "Remote_Characters",
        "Remote_Scenes"
    };

    private static readonly string[] ContentLabels =
    {
        "content.bootstrap",
        BootstrapRequiredLabel,
        "content.client-config",
        "content.release-manifest",
        "content.hotfix-metadata",
        "content.hotfix-tools",
        "content.hotfix-symbols",
        "content.ui",
        "content.items",
        "content.item-icons",
        "content.characters",
        "content.scenes"
    };

    [MenuItem("GameWithLLM/Hot Update/Configure Addressables A1")]
    public static void Configure()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException(
                                                "AddressableAssetSettings is not configured.");

        ConfigureProfiles(settings);
        settings.BuildRemoteCatalog = true;
        settings.DisableCatalogUpdateOnStartup = true;
        settings.RemoteCatalogBuildPath.SetVariableByName(
            settings,
            RemoteCatalogBuildPathVariable);
        settings.RemoteCatalogLoadPath.SetVariableByName(
            settings,
            RemoteCatalogLoadPathVariable);
        settings.CatalogRequestsTimeout = 15;
        settings.BundleTimeout = 30;
        settings.BundleRetryCount = 2;
        settings.UniqueBundleIds = true;

        AddressableAssetGroup defaultGroup = settings.DefaultGroup;
        if (defaultGroup != null &&
            string.Equals(defaultGroup.Name, "Default Local Group", StringComparison.Ordinal) &&
            defaultGroup.entries.Count == 0)
        {
            defaultGroup.Name = "Local_Bootstrap";
        }

        foreach (string groupName in LocalGroups)
            ConfigureGroup(settings, EnsureGroup(settings, groupName), false);
        foreach (string groupName in RemoteGroups)
            ConfigureGroup(settings, EnsureGroup(settings, groupName), true);

        settings.DefaultGroup = settings.FindGroup("Local_Bootstrap");
        foreach (string label in ContentLabels)
            settings.AddLabel(label, false);
        ConfigureBootstrapProbe(settings);

        EnableBootstrapInSampleScene();
        settings.SetDirty(
            AddressableAssetSettings.ModificationEvent.BatchModification,
            null,
            true,
            true);
        AssetDatabase.SaveAssets();
        Debug.Log("[Content] Addressables A1 profiles, catalog and owner Groups configured.");
    }

    [MenuItem("GameWithLLM/Hot Update/Verify Addressables A1")]
    public static void Verify()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException(
                                                "AddressableAssetSettings is not configured.");
        var errors = new List<string>();
        if (!settings.BuildRemoteCatalog)
            errors.Add("Remote Catalog is disabled.");
        if (!settings.DisableCatalogUpdateOnStartup)
            errors.Add("Catalog update-on-start must be disabled so ClientContentBootstrap owns the check.");
        if (settings.RemoteCatalogBuildPath.GetName(settings) != RemoteCatalogBuildPathVariable)
            errors.Add("Remote Catalog BuildPath does not use the stable catalog path variable.");
        if (settings.RemoteCatalogLoadPath.GetName(settings) != RemoteCatalogLoadPathVariable)
            errors.Add("Remote Catalog LoadPath does not use the stable catalog path variable.");
        if (settings.DefaultGroup == null || settings.DefaultGroup.Name != "Local_Bootstrap")
            errors.Add("Local_Bootstrap is not the default Addressables Group.");
        if (settings.profileSettings.GetProfileName(settings.activeProfileId) != "LocalDevelopment")
            errors.Add("LocalDevelopment must remain the checked-in active Profile.");
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            errors.Add("The active build target must be Windows x86_64 (StandaloneWindows64).");

        var buildPaths = new HashSet<string>(StringComparer.Ordinal);
        var loadPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (string profileName in new[] { "LocalDevelopment", "QA", "Production" })
        {
            string profileId = settings.profileSettings.GetProfileId(profileName);
            if (string.IsNullOrEmpty(profileId))
            {
                errors.Add($"Profile '{profileName}' is missing.");
                continue;
            }

            string releaseId = settings.profileSettings.GetValueByName(profileId, ReleaseIdVariable);
            string buildPath = settings.profileSettings.GetValueByName(
                profileId,
                AddressableAssetSettings.kRemoteBuildPath);
            string loadPath = settings.profileSettings.GetValueByName(
                profileId,
                AddressableAssetSettings.kRemoteLoadPath);
            string catalogBuildPath = settings.profileSettings.GetValueByName(
                profileId,
                RemoteCatalogBuildPathVariable);
            string catalogLoadPath = settings.profileSettings.GetValueByName(
                profileId,
                RemoteCatalogLoadPathVariable);

            if (string.IsNullOrWhiteSpace(releaseId))
                errors.Add($"Profile '{profileName}' has no ReleaseId.");
            if (!buildPath.Contains("[ReleaseId]", StringComparison.Ordinal) ||
                !loadPath.Contains("[ReleaseId]", StringComparison.Ordinal))
                errors.Add($"Profile '{profileName}' remote bundle paths are not release-versioned.");
            if (catalogBuildPath.Contains("[ReleaseId]", StringComparison.Ordinal) ||
                catalogLoadPath.Contains("[ReleaseId]", StringComparison.Ordinal))
                errors.Add($"Profile '{profileName}' catalog path must be stable across releases.");
            if (!Uri.TryCreate(loadPath.Replace("[BuildTarget]", "StandaloneWindows64")
                    .Replace("[ReleaseId]", releaseId), UriKind.Absolute, out _))
                errors.Add($"Profile '{profileName}' has an invalid Remote.LoadPath.");
            if (!Uri.TryCreate(catalogLoadPath.Replace("[BuildTarget]", "StandaloneWindows64"),
                    UriKind.Absolute, out _))
                errors.Add($"Profile '{profileName}' has an invalid RemoteCatalog.LoadPath.");
            if (!buildPaths.Add(buildPath))
                errors.Add($"Profile '{profileName}' shares its Remote.BuildPath with another profile.");
            if (!loadPaths.Add(loadPath))
                errors.Add($"Profile '{profileName}' shares its Remote.LoadPath with another profile.");
        }

        foreach (string groupName in LocalGroups.Concat(RemoteGroups))
        {
            AddressableAssetGroup group = settings.FindGroup(groupName);
            if (group == null)
            {
                errors.Add($"Owner Group '{groupName}' is missing.");
                continue;
            }
            BundledAssetGroupSchema bundled = group.GetSchema<BundledAssetGroupSchema>();
            ContentUpdateGroupSchema update = group.GetSchema<ContentUpdateGroupSchema>();
            if (bundled == null || update == null)
            {
                errors.Add($"Owner Group '{groupName}' is missing required schemas.");
                continue;
            }

            bool remote = groupName.StartsWith("Remote_", StringComparison.Ordinal);
            string expectedBuild = remote
                ? AddressableAssetSettings.kRemoteBuildPath
                : AddressableAssetSettings.kLocalBuildPath;
            string expectedLoad = remote
                ? AddressableAssetSettings.kRemoteLoadPath
                : AddressableAssetSettings.kLocalLoadPath;
            if (bundled.BuildPath.GetName(settings) != expectedBuild ||
                bundled.LoadPath.GetName(settings) != expectedLoad)
                errors.Add($"Owner Group '{groupName}' uses the wrong path pair.");
            if (bundled.AssetBundledCacheClearBehavior !=
                BundledAssetGroupSchema.CacheClearBehavior.ClearWhenSpaceIsNeededInCache)
                errors.Add($"Owner Group '{groupName}' enables early automatic cache cleanup.");
            if (remote && bundled.BundleNaming != BundledAssetGroupSchema.BundleNamingStyle.OnlyHash)
                errors.Add($"Remote Group '{groupName}' does not use immutable hash bundle names.");
        }

        AddressableAssetEntry bootstrapProbe = settings.FindAssetEntry(
            AssetDatabase.AssetPathToGUID(BootstrapProbePath));
        if (bootstrapProbe == null || bootstrapProbe.parentGroup?.Name != "Remote_ClientConfig" ||
            bootstrapProbe.address != BootstrapProbeAddress ||
            !bootstrapProbe.labels.Contains(BootstrapRequiredLabel))
        {
            errors.Add("The A1 bootstrap delivery probe is not correctly addressable.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException(
                "Addressables A1 validation failed:\n- " + string.Join("\n- ", errors));

        Debug.Log(
            "[Content] Addressables A1 verified: 3 deployment profiles, stable remote catalog, " +
            $"{LocalGroups.Length} local Groups and {RemoteGroups.Length} remote Groups.");
    }

    public static void BuildAllWindowsProfilesFromCommandLine()
    {
        try
        {
            Configure();
            Verify();
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
            string originalProfile = settings.activeProfileId;
            try
            {
                foreach (string profileName in new[] { "LocalDevelopment", "QA", "Production" })
                {
                    settings.activeProfileId = settings.profileSettings.GetProfileId(profileName);
                    AddressableAssetSettings.BuildPlayerContent(
                        out AddressablesPlayerBuildResult result);
                    if (!string.IsNullOrWhiteSpace(result.Error))
                        throw new InvalidOperationException(
                            $"Addressables build for '{profileName}' failed: {result.Error}");

                    string rawCatalogPath = settings.profileSettings.GetValueByName(
                        settings.activeProfileId,
                        RemoteCatalogBuildPathVariable);
                    string catalogPath = settings.profileSettings.EvaluateString(
                        settings.activeProfileId,
                        rawCatalogPath);
                    Debug.Log(
                        $"[Content] Built Windows Addressables profile '{profileName}' " +
                        $"to stable catalog path '{catalogPath}'.");
                }
            }
            finally
            {
                settings.activeProfileId = originalProfile;
                if (!string.IsNullOrEmpty(originalProfile))
                {
                    AddressableAssetSettings.BuildPlayerContent(
                        out AddressablesPlayerBuildResult activeResult);
                    if (!string.IsNullOrWhiteSpace(activeResult.Error))
                        throw new InvalidOperationException(
                            $"Addressables rebuild for the active profile failed: {activeResult.Error}");
                }
                AssetDatabase.SaveAssets();
            }
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    public static void ConfigureAndVerifyFromCommandLine()
    {
        try
        {
            Configure();
            Verify();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    public static void VerifyFromCommandLine()
    {
        try
        {
            Verify();
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    private static void ConfigureProfiles(AddressableAssetSettings settings)
    {
        AddressableAssetProfileSettings profiles = settings.profileSettings;
        profiles.CreateValue(ReleaseIdVariable, "a1-bootstrap");
        profiles.CreateValue(RemoteCatalogBuildPathVariable, "ServerData/default/[BuildTarget]/catalog");
        profiles.CreateValue(RemoteCatalogLoadPathVariable,
            "http://127.0.0.1:8081/ServerData/default/[BuildTarget]/catalog");

        string defaultProfile = profiles.GetProfileId("Default");
        if (!string.IsNullOrEmpty(defaultProfile))
        {
            ConfigureProfile(
                profiles,
                defaultProfile,
                "default",
                "http://127.0.0.1:8081/ServerData/default");
        }

        string sourceProfile = settings.activeProfileId;
        ConfigureProfile(
            profiles,
            profiles.AddProfile("LocalDevelopment", sourceProfile),
            "local",
            "http://127.0.0.1:8081/ServerData/local");
        ConfigureProfile(
            profiles,
            profiles.AddProfile("QA", sourceProfile),
            "qa",
            "https://content-qa.gamewithllm.dev");
        ConfigureProfile(
            profiles,
            profiles.AddProfile("Production", sourceProfile),
            "production",
            "https://content.gamewithllm.dev");
        settings.activeProfileId = profiles.GetProfileId("LocalDevelopment");
    }

    private static void ConfigureProfile(
        AddressableAssetProfileSettings profiles,
        string profileId,
        string channel,
        string contentHost)
    {
        profiles.SetValue(profileId, ReleaseIdVariable, "a1-bootstrap");
        profiles.SetValue(
            profileId,
            AddressableAssetSettings.kRemoteBuildPath,
            $"ServerData/{channel}/[BuildTarget]/[ReleaseId]");
        profiles.SetValue(
            profileId,
            AddressableAssetSettings.kRemoteLoadPath,
            $"{contentHost}/[BuildTarget]/[ReleaseId]");
        profiles.SetValue(
            profileId,
            RemoteCatalogBuildPathVariable,
            $"ServerData/{channel}/[BuildTarget]/catalog");
        profiles.SetValue(
            profileId,
            RemoteCatalogLoadPathVariable,
            $"{contentHost}/[BuildTarget]/catalog");
    }

    private static AddressableAssetGroup EnsureGroup(
        AddressableAssetSettings settings,
        string groupName)
    {
        AddressableAssetGroup group = settings.FindGroup(groupName);
        if (group != null)
            return group;
        return settings.CreateGroup(
            groupName,
            false,
            false,
            false,
            null,
            typeof(ContentUpdateGroupSchema),
            typeof(BundledAssetGroupSchema));
    }

    private static void ConfigureGroup(
        AddressableAssetSettings settings,
        AddressableAssetGroup group,
        bool remote)
    {
        BundledAssetGroupSchema bundled = group.GetSchema<BundledAssetGroupSchema>() ??
                                           group.AddSchema<BundledAssetGroupSchema>();
        ContentUpdateGroupSchema update = group.GetSchema<ContentUpdateGroupSchema>() ??
                                          group.AddSchema<ContentUpdateGroupSchema>();
        bundled.BuildPath.SetVariableByName(
            settings,
            remote ? AddressableAssetSettings.kRemoteBuildPath : AddressableAssetSettings.kLocalBuildPath);
        bundled.LoadPath.SetVariableByName(
            settings,
            remote ? AddressableAssetSettings.kRemoteLoadPath : AddressableAssetSettings.kLocalLoadPath);
        bundled.BundleNaming = remote
            ? BundledAssetGroupSchema.BundleNamingStyle.OnlyHash
            : BundledAssetGroupSchema.BundleNamingStyle.FileNameHash;
        bundled.AssetBundledCacheClearBehavior =
            BundledAssetGroupSchema.CacheClearBehavior.ClearWhenSpaceIsNeededInCache;
        bundled.UseAssetBundleCache = true;
        update.StaticContent = !remote;
        EditorUtility.SetDirty(group);
        EditorUtility.SetDirty(bundled);
        EditorUtility.SetDirty(update);
    }

    private static void ConfigureBootstrapProbe(AddressableAssetSettings settings)
    {
        string guid = AssetDatabase.AssetPathToGUID(BootstrapProbePath);
        if (string.IsNullOrEmpty(guid))
            throw new InvalidOperationException(
                $"A1 bootstrap delivery probe is missing: {BootstrapProbePath}");
        AddressableAssetGroup group = settings.FindGroup("Remote_ClientConfig") ??
                                      throw new InvalidOperationException(
                                          "Remote_ClientConfig Group is missing.");
        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = BootstrapProbeAddress;
        entry.SetLabel(BootstrapRequiredLabel, true, false, false);
    }

    private static void EnableBootstrapInSampleScene()
    {
        const string scenePath = "Assets/Scenes/SampleScene.unity";
        UnityEngine.SceneManagement.Scene scene = EditorSceneManager.OpenScene(
            scenePath,
            OpenSceneMode.Single);
        AgentHostClient host = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<AgentHostClient>(true))
            .FirstOrDefault();
        if (host == null)
            throw new InvalidOperationException("SampleScene has no AgentHostClient.");

        var serializedHost = new SerializedObject(host);
        SerializedProperty enabledProperty =
            serializedHost.FindProperty("enableContentBootstrap") ??
            throw new InvalidOperationException(
                "AgentHostClient.enableContentBootstrap is not serialized.");
        enabledProperty.boolValue = true;
        serializedHost.ApplyModifiedPropertiesWithoutUndo();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }
}
