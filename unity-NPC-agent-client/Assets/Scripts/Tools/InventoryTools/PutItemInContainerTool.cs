using GameWithLLM.AgentRuntime;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class PutItemInContainerTool : InventoryNpcTool<PutItemInContainerArgs>
{
    public override string Name => "game_inventory_put_item";
    public override string Description =>
        "把当前 NPC 自身背包中的指定物品原子转移到附近容器；containerId 应来自 game_inventory_get_nearby_containers。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        PutItemInContainerArgs args)
    {
        InventoryComponent source = InventoryToolSupport.RequireNpcInventory(npc);
        NearbyInventoryContainer target = InventoryToolSupport.RequireNearbyContainer(npc, args.containerId);
        ItemData item = InventoryToolSupport.RequireItem(source, args.itemId, "ITEM_NOT_OWNED");
        JToken data = InventoryToolSupport.TransferItem(
            source,
            target.Inventory,
            item,
            args.quantity,
            "INSUFFICIENT_ITEM_QUANTITY",
            "TARGET_INVENTORY_FULL");
        return Success(data, $"已将 {args.quantity} 个 '{item.ItemName}' 放入 '{target.ContainerId}'。");
    }
}
