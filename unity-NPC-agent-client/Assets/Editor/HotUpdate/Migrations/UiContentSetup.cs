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
using UnityEngine;
using UnityEngine.UIElements;

public static class UiContentSetup
{
    private const string GroupName = "Remote_UI";
    private const string RequiredLabel = "content.ui-required";

    private static readonly IReadOnlyDictionary<string, string> AssetsByAddress =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UiContentIds.PanelSettings] = "Assets/Art/UI/PanelSettings.asset",
            [UiContentIds.GameplayHud] = "Assets/Art/UI/Hud/GameplayHud.uxml",
            [UiContentIds.ChatWindow] = "Assets/Art/UI/ChatView.uxml",
            [UiContentIds.InventoryListWindow] = "Assets/Art/UI/InventoryListView.uxml",
            [UiContentIds.InventoryInteractWindow] = "Assets/Art/UI/InventoryInteractView.uxml",
            [UiContentIds.InventoryWindow] = "Assets/Art/UI/InventoryView.uxml",
            [UiContentIds.ItemDispenserWindow] = "Assets/Art/UI/ItemDispenserView.uxml",
            [UiContentIds.SaveGameWindow] = "Assets/Art/UI/SaveGameView.uxml",
            [UiContentIds.SystemMessageTemplate] = "Assets/Art/UI/Templates/Chat/SystemMessage.uxml",
            [UiContentIds.PlayerMessageTemplate] = "Assets/Art/UI/Templates/Chat/PlayerMessage.uxml",
            [UiContentIds.OpponentMessageTemplate] = "Assets/Art/UI/Templates/Chat/OpponentMessage.uxml",
            [UiContentIds.InventorySlotTemplate] = "Assets/Art/UI/Templates/Inventory/InventorySlot.uxml",
            ["ui/style/chat"] = "Assets/Art/UI/ChatStyle.uss",
            ["ui/style/inventory"] = "Assets/Art/UI/InventoryStyle.uss",
            ["ui/style/item-dispenser"] = "Assets/Art/UI/ItemDispenserStyle.uss",
            ["ui/style/save-game"] = "Assets/Art/UI/SaveGameStyle.uss",
            ["ui/style/gameplay-hud"] = "Assets/Art/UI/Hud/GameplayHud.uss",
            ["ui/theme/default-runtime"] = "Assets/UI Toolkit/UnityThemes/UnityDefaultRuntimeTheme.tss"
        };

    public static void Configure()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        settings.AddLabel(RequiredLabel, false);
        foreach (KeyValuePair<string, string> asset in AssetsByAddress)
        {
            string guid = AssetDatabase.AssetPathToGUID(asset.Value);
            if (string.IsNullOrWhiteSpace(guid))
                throw new FileNotFoundException($"UI asset is not imported: '{asset.Value}'.");
            AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = asset.Key;
            entry.SetLabel(RequiredLabel, true, false, false);
        }

        settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
        AssetDatabase.SaveAssets();
        Verify();
        Debug.Log($"[Content] Addressables UI configured: {AssetsByAddress.Count} assets.");
    }

    public static void Verify()
    {
        AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.Settings ??
                                            throw new InvalidOperationException("Addressables settings are missing.");
        AddressableAssetGroup group = settings.FindGroup(GroupName) ??
                                      throw new InvalidOperationException($"Addressables Group '{GroupName}' is missing.");
        if (!settings.GetLabels().Contains(RequiredLabel))
            throw new InvalidDataException($"Addressables label '{RequiredLabel}' is missing.");
        var expectedPaths = new HashSet<string>(AssetsByAddress.Values, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> asset in AssetsByAddress)
        {
            AddressableAssetEntry entry = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(asset.Value));
            if (entry == null || entry.parentGroup != group || entry.address != asset.Key ||
                !entry.labels.Contains(RequiredLabel))
                throw new InvalidDataException($"UI Addressable entry '{asset.Key}' is invalid.");

            if (AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(asset.Value) is VisualTreeAsset visualTree)
                UiContentContractValidator.ValidateAsset(asset.Key, visualTree);
        }

        if (Directory.Exists("Assets/Resources/UI"))
            throw new InvalidDataException("UI assets still exist under Assets/Resources/UI.");

        string scenePath = "Assets/Scenes/SampleScene.unity";
        string[] sceneDependencies = AssetDatabase.GetDependencies(scenePath, true);
        string hardReference = sceneDependencies.FirstOrDefault(expectedPaths.Contains);
        if (hardReference != null)
            throw new InvalidDataException(
                $"SampleScene still directly references remote UI asset '{hardReference}'.");

        foreach (AnalyzeRule rule in new AnalyzeRule[]
                 {
                     new CheckSceneDupeDependencies(),
                     new CheckResourcesDupeDependencies()
                 })
        {
            AnalyzeRule.AnalyzeResult duplicate = rule.RefreshAnalysis(settings)
                .FirstOrDefault(result =>
                    result.severity == MessageType.Warning &&
                    expectedPaths.Any(path =>
                        result.resultName.Contains(path, StringComparison.Ordinal)));
            if (duplicate != null)
                throw new InvalidDataException(
                    $"Addressables Analyze reported duplicate UI packing: {duplicate.resultName}");
        }

        string scriptsRoot = Path.Combine(Application.dataPath, "Scripts", "UIManager");
        foreach (string script in Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(script).Contains("Resources.Load", StringComparison.Ordinal))
                throw new InvalidDataException($"Production UI still uses Resources.Load: '{script}'.");
        }

        Debug.Log($"[Content] Addressables UI verified: {AssetsByAddress.Count} assets and contracts.");
    }

}
