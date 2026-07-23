using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

internal sealed class NearbyInventoryContainer
{
    public InventoryComponent Inventory { get; }
    public string ContainerId { get; }
    public string DisplayName { get; }
    public float Distance { get; }

    public NearbyInventoryContainer(
        InventoryComponent inventory,
        string containerId,
        string displayName,
        float distance)
    {
        Inventory = inventory;
        ContainerId = containerId;
        DisplayName = displayName;
        Distance = distance;
    }
}

internal static class InventoryToolSupport
{
    public static ItemDataList RequireItemCatalog()
    {
        ItemDataList catalog = InventoryViewModel.Instance.ItemCatalog;
        if (catalog == null || catalog.items == null)
        {
            throw new ToolExecutionException(
                "ITEM_CATALOG_UNAVAILABLE",
                "当前游戏未配置可用的物品静态数据表。");
        }
        return catalog;
    }

    public static InventoryComponent RequireNpcInventory(NpcToolContext context)
    {
        InventoryComponent inventory = context.Npc.GetComponent<InventoryComponent>();
        if (inventory == null)
        {
            throw new ToolExecutionException(
                "NPC_INVENTORY_MISSING",
                $"NPC '{context.Npc.npcId}' 没有 InventoryComponent。");
        }
        return inventory;
    }

    public static NearbyInventoryContainer RequireNearbyContainer(
        NpcToolContext context,
        string requestedContainerId)
    {
        InventoryComponent selfInventory = RequireNpcInventory(context);
        var matches = new List<(InventoryComponent inventory, string displayName)>();
        IReadOnlyList<(InventoryComponent component, string name)> containers =
            InventoryViewModel.Instance.GetAllContainers();

        for (int i = 0; i < containers.Count; i++)
        {
            InventoryComponent candidate = containers[i].component;
            if (candidate == null)
                continue;

            string displayName = containers[i].name;
            if (MatchesContainer(candidate, displayName, requestedContainerId))
                matches.Add((candidate, displayName));
        }

        if (matches.Count == 0)
        {
            throw new ToolExecutionException(
                "CONTAINER_NOT_FOUND",
                $"未找到标识为 '{requestedContainerId}' 的已注册容器。");
        }

        InventoryComponent target = matches[0].inventory;
        for (int i = 1; i < matches.Count; i++)
        {
            if (matches[i].inventory != target)
            {
                throw new ToolExecutionException(
                    "CONTAINER_ID_AMBIGUOUS",
                    $"容器标识 '{requestedContainerId}' 不唯一，请为容器配置唯一 containerId。");
            }
        }

        if (target == selfInventory)
        {
            throw new ToolExecutionException(
                "TARGET_IS_SELF",
                "目标容器不能是 NPC 自身背包；请使用获取自身背包工具。");
        }

        float distance = DistanceToContainer(context.Npc.transform.position, target);
        float allowedDistance = context.Npc.InventoryInteractionRange;
        if (distance > allowedDistance)
        {
            throw new ToolExecutionException(
                "CONTAINER_TOO_FAR",
                $"容器 '{target.ContainerId}' 距离 NPC {distance:0.##}，" +
                $"超过最大交互距离 {allowedDistance:0.##}。");
        }

        string resolvedDisplayName = string.IsNullOrWhiteSpace(matches[0].displayName)
            ? target.gameObject.name
            : matches[0].displayName;
        return new NearbyInventoryContainer(
            target,
            target.ContainerId,
            resolvedDisplayName,
            distance);
    }

    public static ItemData RequireOwnedItem(InventoryComponent inventory, string itemId)
    {
        IReadOnlyList<InventorySlot> slots = inventory.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            InventorySlot slot = slots[i];
            if (slot == null || slot.IsEmpty || slot.Item == null)
                continue;
            if (string.Equals(slot.Item.ItemId, itemId, StringComparison.OrdinalIgnoreCase))
                return slot.Item;
        }

        throw new ToolExecutionException(
            "ITEM_NOT_OWNED",
            $"自身背包中没有 itemId 为 '{itemId}' 的物品。");
    }

    public static string SerializeItemDefinitions(ItemDataList catalog)
    {
        var items = catalog.items
            .Where(item => item != null)
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(item => new
            {
                itemId = item.ItemId,
                name = item.ItemName,
                description = item.Description,
                maxStackSize = item.MaxStackSize
            })
            .ToList();

        return JsonConvert.SerializeObject(new { itemTypes = items }, Formatting.None);
    }

    public static string SerializeInventory(
        InventoryComponent inventory,
        string displayName,
        float? distance = null)
    {
        var byItem = new Dictionary<string, InventoryItemSummary>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<InventorySlot> slots = inventory.Slots;

        for (int i = 0; i < slots.Count; i++)
        {
            InventorySlot slot = slots[i];
            if (slot == null || slot.IsEmpty || slot.Item == null)
                continue;

            string key = string.IsNullOrWhiteSpace(slot.Item.ItemId)
                ? $"name:{slot.Item.ItemName}"
                : slot.Item.ItemId;
            if (!byItem.TryGetValue(key, out InventoryItemSummary summary))
            {
                summary = new InventoryItemSummary
                {
                    ItemId = slot.Item.ItemId,
                    Name = slot.Item.ItemName
                };
                byItem.Add(key, summary);
            }
            summary.Quantity += slot.Quantity;
        }

        List<InventoryItemSummary> items = byItem.Values
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new
        {
            containerId = inventory.ContainerId,
            displayName,
            distance = distance.HasValue
                ? (float?)Math.Round(distance.Value, 2)
                : null,
            maxSlots = inventory.MaxSlots,
            occupiedSlots = inventory.OccupiedSlotCount,
            emptySlots = inventory.EmptySlotCount,
            items
        };
        return JsonConvert.SerializeObject(result, Formatting.None);
    }

    private static bool MatchesContainer(
        InventoryComponent candidate,
        string displayName,
        string requestedContainerId)
    {
        return string.Equals(
                   candidate.ContainerId,
                   requestedContainerId,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   candidate.gameObject.name,
                   requestedContainerId,
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   displayName,
                   requestedContainerId,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static float DistanceToContainer(Vector3 origin, InventoryComponent container)
    {
        Collider collider = container.GetComponent<Collider>();
        Vector3 destination = collider != null && collider.enabled
            ? collider.ClosestPoint(origin)
            : container.transform.position;
        return Vector3.Distance(origin, destination);
    }

    private sealed class InventoryItemSummary
    {
        [JsonProperty("itemId")] public string ItemId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("quantity")] public int Quantity { get; set; }
    }
}
