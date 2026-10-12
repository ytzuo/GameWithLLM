using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public sealed class ItemContentCatalogTests
{
    [Test]
    public void Validate_AcceptsStableBusinessAndPresentationMappings()
    {
        ItemDataList catalog = ScriptableObject.CreateInstance<ItemDataList>();
        catalog.items = new List<ItemData> { Item("rock", 64, "11") };
        Assert.DoesNotThrow(() => ItemContentCatalog.Validate(catalog));
        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void Validate_RejectsDuplicatePublishedItemId()
    {
        ItemDataList catalog = ScriptableObject.CreateInstance<ItemDataList>();
        catalog.items = new List<ItemData> { Item("rock", 64, "11"), Item("rock", 64, "other") };
        Assert.Throws<InvalidOperationException>(() => ItemContentCatalog.Validate(catalog));
        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void Validate_RejectsInvalidStackRuleAndWorldVisualAddress()
    {
        ItemData item = Item("axe", 0, "weapon_0");
        item.WorldVisualAddress = "prefabs/axe";
        ItemDataList catalog = ScriptableObject.CreateInstance<ItemDataList>();
        catalog.items = new List<ItemData> { item };
        Assert.Throws<InvalidOperationException>(() => ItemContentCatalog.Validate(catalog));
        UnityEngine.Object.DestroyImmediate(catalog);
    }

    [Test]
    public void ParsePresentationTexts_RejectsInvalidHeader()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ItemContentCatalog.ParsePresentationTexts("{\"schemaVersion\":2,\"locale\":\"zh-CN\",\"texts\":{}}"));
    }

    private static ItemData Item(string id, int maxStackSize, string iconName) => new ItemData
    {
        ItemId = id,
        DisplayNameKey = $"item.{id}.name",
        DescriptionKey = $"item.{id}.description",
        IconAtlasAddress = "item/icons/core-atlas",
        IconName = iconName,
        MaxStackSize = maxStackSize
    };
}
