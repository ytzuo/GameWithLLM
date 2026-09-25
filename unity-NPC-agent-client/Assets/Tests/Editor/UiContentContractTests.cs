using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

public sealed class UiContentContractTests
{
    private static readonly IReadOnlyDictionary<string, string> ContractAssets =
        new Dictionary<string, string>
        {
            [UiContentIds.ChatWindow] = "Assets/Art/UI/ChatView.uxml",
            [UiContentIds.InventoryListWindow] = "Assets/Art/UI/InventoryListView.uxml",
            [UiContentIds.InventoryInteractWindow] = "Assets/Art/UI/InventoryInteractView.uxml",
            [UiContentIds.InventoryWindow] = "Assets/Art/UI/InventoryView.uxml",
            [UiContentIds.ItemDispenserWindow] = "Assets/Art/UI/ItemDispenserView.uxml",
            [UiContentIds.SaveGameWindow] = "Assets/Art/UI/SaveGameView.uxml",
            [UiContentIds.SystemMessageTemplate] = "Assets/Art/UI/Templates/Chat/SystemMessage.uxml",
            [UiContentIds.PlayerMessageTemplate] = "Assets/Art/UI/Templates/Chat/PlayerMessage.uxml",
            [UiContentIds.OpponentMessageTemplate] = "Assets/Art/UI/Templates/Chat/OpponentMessage.uxml",
            [UiContentIds.InventorySlotTemplate] = "Assets/Art/UI/Templates/Inventory/InventorySlot.uxml"
        };

    [Test]
    public void MigratedVisualTrees_SatisfyRuntimeContracts()
    {
        foreach (KeyValuePair<string, string> item in ContractAssets)
        {
            VisualTreeAsset asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(item.Value);
            Assert.That(asset, Is.Not.Null, item.Value);
            Assert.DoesNotThrow(() => UiContentContractValidator.ValidateAsset(item.Key, asset));
        }
    }

    [Test]
    public void ProductionUi_HasNoResourcesDirectory()
    {
        Assert.That(Directory.Exists("Assets/Resources/UI"), Is.False);
    }
}
