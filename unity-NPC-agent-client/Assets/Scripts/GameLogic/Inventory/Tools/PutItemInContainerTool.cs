using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class PutItemInContainerTool : InventoryNpcTool<PutItemInContainerArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {
            ""containerId"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""附近容器查询返回的稳定 containerId"" },
            ""itemId"": { ""type"": ""string"", ""minLength"": 1, ""description"": ""从 NPC 自身背包转移的稳定物品标识"" },
            ""quantity"": { ""type"": ""integer"", ""minimum"": 1 }
          },
          ""required"": [""containerId"", ""itemId"", ""quantity""],
          ""additionalProperties"": false
        }");

    public override string Name => "game_inventory_put_item";
    public override string Description =>
        "把当前 NPC 自身背包中的指定物品原子转移到附近容器；containerId 应来自 game_inventory_get_nearby_containers。";
    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, PutItemInContainerArgs args)
    {
        InventoryComponent source = InventoryToolSupport.RequireNpcInventory(context);
        NearbyInventoryContainer target = InventoryToolSupport.RequireNearbyContainer(context, args.containerId);
        ItemData item = InventoryToolSupport.RequireItem(source, args.itemId, "ITEM_NOT_OWNED");
        JToken data = InventoryToolSupport.TransferItem(
            source,
            target.Inventory,
            item,
            args.quantity,
            "INSUFFICIENT_ITEM_QUANTITY",
            "TARGET_INVENTORY_FULL");
        return ToolExecutionResult.Success(data, $"已将 {args.quantity} 个 '{item.ItemName}' 放入 '{target.ContainerId}'。");
    }
}