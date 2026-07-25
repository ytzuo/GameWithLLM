using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class PutItemInContainerTool : NpcTool<PutItemInContainerArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {
            ""containerId"": {
              ""type"": ""string"",
              ""description"": ""目标容器的稳定标识；默认是容器 GameObject 名称""
            },
            ""itemId"": {
              ""type"": ""string"",
              ""description"": ""要从 NPC 自身背包转移的物品唯一标识""
            },
            ""quantity"": {
              ""type"": ""integer"",
              ""minimum"": 1,
              ""description"": ""要转移的数量""
            }
          },
          ""required"": [""containerId"", ""itemId"", ""quantity""],
          ""additionalProperties"": false
        }");

    public override string Name => "game_inventory_put_item";

    public override string Description =>
        "把当前 NPC 自身背包中的指定物品放入其他容器。" +
        "目标容器必须已注册并位于 NPC 交互距离内；操作不会执行部分转移。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(
        NpcToolContext context,
        PutItemInContainerArgs args)
    {
        InventoryComponent source = InventoryToolSupport.RequireNpcInventory(context);
        NearbyInventoryContainer target =
            InventoryToolSupport.RequireNearbyContainer(context, args.containerId);
        ItemData item = InventoryToolSupport.RequireOwnedItem(source, args.itemId);

        int ownedQuantity = source.GetItemCount(item);
        if (ownedQuantity < args.quantity)
        {
            throw new ToolExecutionException(
                "INSUFFICIENT_ITEM_QUANTITY",
                $"NPC 只有 {ownedQuantity} 个 '{item.ItemName}'，无法转移 {args.quantity} 个。");
        }

        int targetCapacity = target.Inventory.GetMaxAddableAmount(item);
        if (targetCapacity < args.quantity)
        {
            throw new ToolExecutionException(
                "TARGET_INVENTORY_FULL",
                $"目标容器 '{target.ContainerId}' 最多还能容纳 {targetCapacity} 个 " +
                $"'{item.ItemName}'，无法转移 {args.quantity} 个。");
        }

        if (!source.TransferItemTo(target.Inventory, item, args.quantity))
        {
            throw new ToolExecutionException(
                "INVENTORY_TRANSFER_FAILED",
                $"无法将 '{item.ItemName}' 转移到容器 '{target.ContainerId}'。");
        }

        return ToolExecutionResult.Success(JToken.FromObject(new
        {
            transferred = true,
            itemId = item.ItemId,
            name = item.ItemName,
            quantity = args.quantity,
            fromContainerId = source.ContainerId,
            toContainerId = target.ContainerId,
            targetDistance = System.Math.Round(target.Distance, 2),
            remainingQuantity = source.GetItemCount(item)
        }));
    }
}
