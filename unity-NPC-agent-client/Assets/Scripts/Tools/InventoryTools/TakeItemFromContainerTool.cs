using GameWithLLM.AgentRuntime;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class TakeItemFromContainerTool : InventoryNpcTool<TakeItemFromContainerArgs>
{
    public override string Name => "game_inventory_take_item";
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
        return Success(data, ClientTextCatalogs.Message(
            "tool.inventory.take_succeeded",
            source.ContainerId,
            args.quantity,
            item.ItemName));
    }
}
