using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.AnalyzeRules;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.Build.Reporting;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

public static class AddressablesA4ProjectSetup
{
    private const string GroupName = "Remote_SpritesTextures";
    private const string ItemLabel = "content.items";
    private const string IconLabel = "content.item-icons";
    private const string CatalogPath = "Assets/Data/Items/ItemDataList.asset";
    private const string AtlasPath = "Assets/Art/Items/ItemCore.spriteatlas";
    private const string AtlasAddress = "item/icons/core-atlas";
    private const string ItemTextCatalogPath = "Assets/Content/Items/items.zh-CN.json";

    private static readonly IReadOnlyDictionary<string, string> IconPaths =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rock"] = "Assets/Art/Items/11.png",
            ["wood"] = "Assets/Art/Items/10979.png",
            ["axe"] = "Assets/Art/Items/weapon_0.png",
            ["helmet"] = "Assets/Art/Items/1086.png"
        };

    private static readonly IReadOnlyDictionary<string, int> PublishedStackRules =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["rock"] = 64,
            ["wood"] = 64,
            ["axe"] = 1,
            ["helmet"] = 1
        };

    [MenuItem("GameWithLLM/Hot Update/Configure Addressables A4 Items")]
    public static void Configure()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        settings.AddLabel(ItemLabel, false);
        settings.AddLabel(IconLabel, false);
        EditorSettings.spritePackerMode = SpritePackerMode.BuildTimeOnlyAtlas;

        SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath);
        if (atlas == null)
        {
            atlas = new SpriteAtlas();
            AssetDatabase.CreateAsset(atlas, AtlasPath);
        }
        SpriteAtlasExtensions.Remove(atlas, SpriteAtlasExtensions.GetPackables(atlas));
        UnityEngine.Object[] icons = IconPaths.Values
            .Select(path => AssetDatabase.LoadAssetAtPath<Sprite>(path) as UnityEngine.Object)
            .ToArray();
        if (icons.Any(icon => icon == null))
            throw new FileNotFoundException("One or more A4 item icons are missing or not imported as Sprite.");
        SpriteAtlasExtensions.Add(atlas, icons);
        EditorUtility.SetDirty(atlas);

        ConfigureEntry(settings, group, CatalogPath, ItemContentIds.Catalog, ItemLabel);
        ConfigureEntry(settings, group, ItemTextCatalogPath, ItemContentIds.TextCatalog, ItemLabel);
        ConfigureEntry(settings, group, AtlasPath, AtlasAddress, IconLabel);
        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        AssetDatabase.SaveAssets();
        Verify();
        Debug.Log("[Content] Addressables A4 item catalog and core icon atlas configured.");
    }

    [MenuItem("GameWithLLM/Hot Update/Verify Addressables A4 Items")]
    public static void Verify()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        if (EditorSettings.spritePackerMode != SpritePackerMode.BuildTimeOnlyAtlas)
            throw new InvalidDataException("A4 requires SpritePackerMode.BuildTimeOnlyAtlas.");
        VerifyEntry(settings, group, CatalogPath, ItemContentIds.Catalog, ItemLabel);
        VerifyEntry(settings, group, ItemTextCatalogPath, ItemContentIds.TextCatalog, ItemLabel);
        VerifyEntry(settings, group, AtlasPath, AtlasAddress, IconLabel);

        ItemDataList catalog = AssetDatabase.LoadAssetAtPath<ItemDataList>(CatalogPath);
        ItemContentCatalog.Validate(catalog);
        string[] expectedIds = PublishedStackRules.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (!catalog.items.Select(item => item.ItemId).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expectedIds))
            throw new InvalidDataException("Published A4 itemId baseline changed; use a tombstone or explicit save migration.");
        foreach (ItemData item in catalog.items)
        {
            if (item.MaxStackSize != PublishedStackRules[item.ItemId])
                throw new InvalidDataException(
                    $"Published MaxStackSize changed for '{item.ItemId}'; validate old saves before changing the baseline.");
        }

        SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(AtlasPath) ??
                            throw new FileNotFoundException($"A4 SpriteAtlas is missing: '{AtlasPath}'.");
        var packedPaths = new HashSet<string>(
            SpriteAtlasExtensions.GetPackables(atlas).Select(AssetDatabase.GetAssetPath),
            StringComparer.Ordinal);
        if (!packedPaths.SetEquals(IconPaths.Values))
            throw new InvalidDataException("A4 SpriteAtlas packables do not match the published item icon set.");

        IReadOnlyDictionary<string, string> texts = ItemContentCatalog.ParsePresentationTexts(
            File.ReadAllText(ItemTextCatalogPath));
        foreach (ItemData item in catalog.items)
        {
            if (!texts.ContainsKey(item.DisplayNameKey) || !texts.ContainsKey(item.DescriptionKey))
                throw new InvalidDataException($"Item text Catalog is missing keys for '{item.ItemId}'.");
        }

        string scenePath = "Assets/Scenes/SampleScene.unity";
        var remotePaths = new HashSet<string>(
            IconPaths.Values.Append(CatalogPath).Append(ItemTextCatalogPath).Append(AtlasPath),
            StringComparer.Ordinal);
        string hardReference = AssetDatabase.GetDependencies(scenePath, true).FirstOrDefault(remotePaths.Contains);
        if (hardReference != null)
            throw new InvalidDataException($"SampleScene still references remote A4 asset '{hardReference}'.");

        foreach (string iconPath in IconPaths.Values)
        {
            AddressableAssetEntry directEntry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(iconPath));
            if (directEntry != null)
                throw new InvalidDataException($"Atlas source icon must not also be a direct Addressable entry: '{iconPath}'.");
        }

        foreach (AnalyzeRule rule in new AnalyzeRule[]
                 {
                     new CheckSceneDupeDependencies(),
                     new CheckResourcesDupeDependencies()
                 })
        {
            AnalyzeRule.AnalyzeResult duplicate = rule.RefreshAnalysis(settings)
                .FirstOrDefault(result => result.severity == MessageType.Warning &&
                                          remotePaths.Any(path => result.resultName.Contains(path, StringComparison.Ordinal)));
            if (duplicate != null)
                throw new InvalidDataException($"Addressables Analyze reported duplicate A4 packing: {duplicate.resultName}");
        }

        Debug.Log("[Content] Addressables A4 verified: stable item IDs, text keys, atlas, ownership and scene boundary.");
    }

    public static void ConfigureFromCommandLine() => RunCommand(Configure);
    public static void VerifyFromCommandLine() => RunCommand(Verify);
    public static void BuildLocalDevelopmentFromCommandLine() => RunCommand(() =>
    {
        Verify();
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings;
        string originalProfile = settings.activeProfileId;
        try
        {
            settings.activeProfileId = settings.profileSettings.GetProfileId("LocalDevelopment");
            AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new InvalidOperationException("A4 Addressables build failed: " + result.Error);
            Debug.Log("[Content] Addressables A4 LocalDevelopment content built.");
        }
        finally
        {
            settings.activeProfileId = originalProfile;
            AssetDatabase.SaveAssets();
        }
    });

    public static void BuildWindowsPlayerFromCommandLine() => RunCommand(() =>
    {
        Verify();
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "AddressablesA4", "GameWithLLM.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/SampleScene.unity" },
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("A4 Windows Player build failed: " + report.summary.result);
        Debug.Log($"[Content] Addressables A4 Windows Player built at '{output}'.");
    });

    private static void ConfigureEntry(AddressableAssetSettings settings, AddressableAssetGroup group, string path, string address, string label)
    {
        string guid = AssetDatabase.AssetPathToGUID(path);
        if (string.IsNullOrWhiteSpace(guid))
            throw new FileNotFoundException($"A4 asset is not imported: '{path}'.");
        AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
        entry.address = address;
        entry.SetLabel(label, true, false, false);
    }

    private static void VerifyEntry(AddressableAssetSettings settings, AddressableAssetGroup group, string path, string address, string label)
    {
        AddressableAssetEntry entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path));
        if (entry == null || entry.parentGroup != group || entry.address != address || !entry.labels.Contains(label))
            throw new InvalidDataException($"A4 Addressable entry '{address}' is invalid.");
    }

    private static void RunCommand(Action action)
    {
        try { action(); EditorApplication.Exit(0); }
        catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
}
