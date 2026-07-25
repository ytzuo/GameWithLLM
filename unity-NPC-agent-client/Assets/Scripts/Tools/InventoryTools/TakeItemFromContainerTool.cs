using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class TakeItemFromContainerTool : InventoryNpcTool<TakeItemFromContainerArgs>
{
    public override string Name => "game_inventory_take_item";
    public override string Description =>
        "从附近容器中取出指定数量的物品放入当前 NPC 背包；操作原子执行，不会部分转移。";

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, TakeItemFromContainerArgs args)
    {
        InventoryComponent target = InventoryToolSupport.RequireNpcInventory(context);
        NearbyInventoryContainer source = InventoryToolSupport.RequireNearbyContainer(context, args.containerId);
        ItemData item = InventoryToolSupport.RequireItem(source.Inventory, args.itemId, "ITEM_NOT_IN_CONTAINER");
        JToken data = InventoryToolSupport.TransferItem(
            source.Inventory,
            target,
            item,
            args.quantity,
            "INSUFFICIENT_CONTAINER_QUANTITY",
            "NPC_INVENTORY_FULL");
        return ToolExecutionResult.Success(data, $"已从 '{source.ContainerId}' 取出 {args.quantity} 个 '{item.ItemName}'。");
    }
}
