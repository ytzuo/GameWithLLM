using GameWithLLM.AgentRuntime;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class TakeItemFromContainerTool : InventoryNpcTool<TakeItemFromContainerArgs>
{
    public override string Name => "game_inventory_take_item";
    public override string Description =>
        "从附近容器中取出指定数量的物品放入当前 NPC 背包；操作原子执行，不会部分转移。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        TakeItemFromContainerArgs args)
    {
        InventoryComponent target = InventoryToolSupport.RequireNpcInventory(npc);
        NearbyInventoryContainer source = InventoryToolSupport.RequireNearbyContainer(npc, args.containerId);
        ItemData item = InventoryToolSupport.RequireItem(source.Inventory, args.itemId, "ITEM_NOT_IN_CONTAINER");
        JToken data = InventoryToolSupport.TransferItem(
            source.Inventory,
            target,
            item,
            args.quantity,
            "INSUFFICIENT_CONTAINER_QUANTITY",
            "NPC_INVENTORY_FULL");
        return Success(data, $"已从 '{source.ContainerId}' 取出 {args.quantity} 个 '{item.ItemName}'。");
    }
}
