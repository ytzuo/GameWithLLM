using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public abstract class InventoryNpcTool<TArgs> : NpcTool<TArgs> where TArgs : ToolArgsBase
{
    public override bool IsAvailable(NpcToolContext context)
    {
        if (context?.Npc == null)
            return false;
        InventoryComponent inventory = context.Npc.GetComponent<InventoryComponent>();
        return inventory != null && inventory.isActiveAndEnabled;
    }
}

internal sealed class NearbyInventoryContainer
{
    public InventoryComponent Inventory { get; }
    public string ContainerId => Inventory.ContainerId;
    public string DisplayName { get; }
    public float Distance { get; }
    public float InteractionRange { get; }
    public bool InRange => Distance <= InteractionRange;
    public NpcTargetRecord OwnerTarget { get; }

    public NearbyInventoryContainer(
        InventoryComponent inventory,
        string displayName,
        float distance,
        float interactionRange,
        NpcTargetRecord ownerTarget)
    {
        Inventory = inventory;
        DisplayName = displayName;
        Distance = distance;
        InteractionRange = interactionRange;
        OwnerTarget = ownerTarget;
    }
}

internal static class InventoryToolSupport
{
    public static ItemDataList RequireItemCatalog()
    {
        ItemDataList catalog = InventoryViewModel.Instance.ItemCatalog;
        if (catalog == null || catalog.items == null)
            throw new ToolExecutionException("ITEM_CATALOG_UNAVAILABLE", "当前游戏未配置可用的物品静态数据表。");
        var itemIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < catalog.items.Count; i++)
        {
            ItemData item = catalog.items[i];
            if (item == null)
                continue;
            if (string.IsNullOrWhiteSpace(item.ItemId))
                throw new ToolExecutionException("ITEM_ID_MISSING", $"物品数据 '{item.ItemName}' 缺少稳定 itemId。");
            if (!itemIds.Add(item.ItemId))
                throw new ToolExecutionException("ITEM_ID_AMBIGUOUS", $"物品标识 '{item.ItemId}' 不唯一，请配置唯一 itemId。");
        }
        return catalog;
    }

    public static InventoryComponent RequireNpcInventory(NpcToolContext context)
    {
        InventoryComponent inventory = context.Npc.GetComponent<InventoryComponent>();
        if (inventory == null)
            throw new ToolExecutionException("NPC_INVENTORY_MISSING", $"NPC '{context.Npc.npcId}' 没有 InventoryComponent。");
        return inventory;
    }

    public static List<NearbyInventoryContainer> GetContainers(
        NpcToolContext context,
        float maxDistance = 0f,
        bool inRangeOnly = false)
    {
        InventoryComponent selfInventory = RequireNpcInventory(context);
        IReadOnlyList<(InventoryComponent component, string name)> registered = InventoryViewModel.Instance.GetAllContainers();
        ValidateUniqueContainerIds(registered);
        var result = new List<NearbyInventoryContainer>();

        for (int i = 0; i < registered.Count; i++)
        {
            InventoryComponent inventory = registered[i].component;
            if (inventory == null || inventory == selfInventory)
                continue;
            float distance = DistanceToContainer(context.Npc.transform.position, inventory);
            if (maxDistance > 0f && distance > maxDistance)
                continue;
            string displayName = string.IsNullOrWhiteSpace(registered[i].name)
                ? inventory.gameObject.name
                : registered[i].name;
            var nearby = new NearbyInventoryContainer(
                inventory,
                displayName,
                distance,
                context.Npc.InventoryInteractionRange,
                NpcTargetSupport.FindOwnerTarget(inventory.gameObject));
            if (!inRangeOnly || nearby.InRange)
                result.Add(nearby);
        }

        return result
            .OrderBy(container => container.Distance)
            .ThenBy(container => container.ContainerId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static NearbyInventoryContainer RequireNearbyContainer(NpcToolContext context, string requestedContainerId)
    {
        InventoryComponent selfInventory = RequireNpcInventory(context);
        IReadOnlyList<(InventoryComponent component, string name)> registered = InventoryViewModel.Instance.GetAllContainers();
        ValidateUniqueContainerIds(registered);
        (InventoryComponent component, string name)? match = null;
        for (int i = 0; i < registered.Count; i++)
        {
            InventoryComponent candidate = registered[i].component;
            if (candidate != null && string.Equals(candidate.ContainerId, requestedContainerId, StringComparison.OrdinalIgnoreCase))
            {
                match = registered[i];
                break;
            }
        }

        if (!match.HasValue)
            throw new ToolExecutionException("CONTAINER_NOT_FOUND", $"未找到 containerId 为 '{requestedContainerId}' 的已注册容器。请先查询附近容器。");
        if (match.Value.component == selfInventory)
            throw new ToolExecutionException("TARGET_IS_SELF", "目标容器不能是 NPC 自身背包；请使用获取自身背包工具。");

        InventoryComponent target = match.Value.component;
        float distance = DistanceToContainer(context.Npc.transform.position, target);
        string displayName = string.IsNullOrWhiteSpace(match.Value.name) ? target.gameObject.name : match.Value.name;
        var resolved = new NearbyInventoryContainer(
            target,
            displayName,
            distance,
            context.Npc.InventoryInteractionRange,
            NpcTargetSupport.FindOwnerTarget(target.gameObject));
        if (!resolved.InRange)
        {
            throw new ToolExecutionException(
                "CONTAINER_TOO_FAR",
                $"容器 '{resolved.ContainerId}' 距离 NPC {distance:0.##} 米，超过最大交互距离 {resolved.InteractionRange:0.##} 米。",
                CreateContainerReferenceData(resolved));
        }
        return resolved;
    }

    public static ItemData RequireItem(InventoryComponent inventory, string itemId, string missingErrorCode)
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
        throw new ToolExecutionException(missingErrorCode, $"容器 '{inventory.ContainerId}' 中没有 itemId 为 '{itemId}' 的物品。");
    }

    public static JToken TransferItem(
        InventoryComponent source,
        InventoryComponent target,
        ItemData item,
        int quantity,
        string sourceQuantityError,
        string targetCapacityError)
    {
        int ownedQuantity = source.GetItemCount(item);
        if (ownedQuantity < quantity)
            throw new ToolExecutionException(sourceQuantityError, $"源容器只有 {ownedQuantity} 个 '{item.ItemName}'，无法转移 {quantity} 个。");
        int targetCapacity = target.GetMaxAddableAmount(item);
        if (targetCapacity < quantity)
            throw new ToolExecutionException(targetCapacityError, $"目标容器最多还能容纳 {targetCapacity} 个 '{item.ItemName}'，无法转移 {quantity} 个。");
        if (!source.TransferItemTo(target, item, quantity))
            throw new ToolExecutionException("INVENTORY_TRANSFER_FAILED", $"无法将 '{item.ItemName}' 从 '{source.ContainerId}' 转移到 '{target.ContainerId}'。");

        return JToken.FromObject(new
        {
            transferred = true,
            itemId = item.ItemId,
            name = item.ItemName,
            quantity,
            fromContainerId = source.ContainerId,
            toContainerId = target.ContainerId,
            sourceInventory = CreateInventoryData(source, source.gameObject.name),
            targetInventory = CreateInventoryData(target, target.gameObject.name)
        });
    }

    public static JToken CreateNearbyContainersData(List<NearbyInventoryContainer> containers)
    {
        return JToken.FromObject(new
        {
            count = containers.Count,
            containers = containers.Select(CreateContainerReferenceData).ToList()
        });
    }

    public static JToken CreateContainerReferenceData(NearbyInventoryContainer container)
    {
        return JToken.FromObject(new
        {
            containerId = container.ContainerId,
            displayName = container.DisplayName,
            ownerTargetId = container.OwnerTarget?.TargetId,
            ownerCategory = container.OwnerTarget?.Category,
            distance = Math.Round(container.Distance, 2),
            interactionRange = Math.Round(container.InteractionRange, 2),
            inRange = container.InRange,
            maxSlots = container.Inventory.MaxSlots,
            occupiedSlots = container.Inventory.OccupiedSlotCount,
            emptySlots = container.Inventory.EmptySlotCount
        });
    }

    public static JToken CreateItemDefinitionsData(ItemDataList catalog)
    {
        var items = catalog.items
            .Where(item => item != null)
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .Select(item => new { itemId = item.ItemId, name = item.ItemName, description = item.Description, maxStackSize = item.MaxStackSize })
            .ToList();
        return JToken.FromObject(new { itemTypes = items });
    }

    public static JToken CreateInventoryData(InventoryComponent inventory, string displayName, float? distance = null)
    {
        var byItem = new Dictionary<string, InventoryItemSummary>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<InventorySlot> slots = inventory.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            InventorySlot slot = slots[i];
            if (slot == null || slot.IsEmpty || slot.Item == null)
                continue;
            string key = string.IsNullOrWhiteSpace(slot.Item.ItemId) ? $"name:{slot.Item.ItemName}" : slot.Item.ItemId;
            if (!byItem.TryGetValue(key, out InventoryItemSummary summary))
            {
                summary = new InventoryItemSummary { ItemId = slot.Item.ItemId, Name = slot.Item.ItemName };
                byItem.Add(key, summary);
            }
            summary.Quantity += slot.Quantity;
        }

        List<InventoryItemSummary> items = byItem.Values
            .OrderBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return JToken.FromObject(new
        {
            containerId = inventory.ContainerId,
            displayName,
            distance = distance.HasValue ? (float?)Math.Round(distance.Value, 2) : null,
            maxSlots = inventory.MaxSlots,
            occupiedSlots = inventory.OccupiedSlotCount,
            emptySlots = inventory.EmptySlotCount,
            items
        });
    }

    private static void ValidateUniqueContainerIds(IReadOnlyList<(InventoryComponent component, string name)> containers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < containers.Count; i++)
        {
            InventoryComponent component = containers[i].component;
            if (component == null)
                continue;
            if (string.IsNullOrWhiteSpace(component.ContainerId))
            {
                throw new ToolExecutionException(
                    "CONTAINER_ID_MISSING",
                    $"容器 '{component.gameObject.name}' 缺少稳定 containerId。");
            }
            if (!seen.Add(component.ContainerId))
                throw new ToolExecutionException("CONTAINER_ID_AMBIGUOUS", $"容器标识 '{component.ContainerId}' 不唯一，请配置唯一 containerId。");
        }
    }

    private static float DistanceToContainer(Vector3 origin, InventoryComponent container)
    {
        Collider collider = container.GetComponent<Collider>();
        Vector3 destination = container.transform.position;
        if (collider != null && collider.enabled)
        {
            // Unity 的 Collider.ClosestPoint 不支持非凸 MeshCollider，直接调用会在
            // 查询附近容器时产生运行时错误，并让后续物品交付使用不可靠的距离。
            // 对该类型使用世界空间包围盒作为稳定的距离近似。
            MeshCollider meshCollider = collider as MeshCollider;
            destination = meshCollider != null && !meshCollider.convex
                ? collider.bounds.ClosestPoint(origin)
                : collider.ClosestPoint(origin);
        }
        return Vector3.Distance(origin, destination);
    }

    private sealed class InventoryItemSummary
    {
        [JsonProperty("itemId")] public string ItemId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("quantity")] public int Quantity { get; set; }
    }
}
