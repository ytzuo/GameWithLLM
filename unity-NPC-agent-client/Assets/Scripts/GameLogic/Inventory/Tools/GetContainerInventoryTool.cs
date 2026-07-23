using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class GetContainerInventoryTool : NpcTool<ContainerInventoryArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {
            ""containerId"": {
              ""type"": ""string"",
              ""description"": ""目标容器的稳定标识；默认是容器 GameObject 名称""
            }
          },
          ""required"": [""containerId""],
          ""additionalProperties"": false
        }");

    public override string Name => "game_inventory_get_container";

    public override string Description =>
        "获取指定容器中的全部物品及数量。目标必须是已注册的 InventoryComponent，" +
        "且位于当前 NPC 的交互距离内；不能用此工具查询 NPC 自身背包。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override string ExecuteCore(
        NpcToolContext context,
        ContainerInventoryArgs args)
    {
        NearbyInventoryContainer container =
            InventoryToolSupport.RequireNearbyContainer(context, args.containerId);
        return InventoryToolSupport.SerializeInventory(
            container.Inventory,
            container.DisplayName,
            container.Distance);
    }
}
