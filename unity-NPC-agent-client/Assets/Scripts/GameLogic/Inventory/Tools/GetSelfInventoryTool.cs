using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class GetSelfInventoryTool : NpcTool<EmptyInventoryToolArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {},
          ""additionalProperties"": false
        }");

    public override string Name => "game_inventory_get_self";

    public override string Description =>
        "获取当前 NPC 自身背包中的全部物品及数量。只查询当前对话 NPC，不查询其他容器。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(
        NpcToolContext context,
        EmptyInventoryToolArgs args)
    {
        InventoryComponent inventory = InventoryToolSupport.RequireNpcInventory(context);
        string displayName = InventoryViewModel.Instance.GetContainerName(inventory);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = context.Npc.gameObject.name;

        return ToolExecutionResult.Success(InventoryToolSupport.CreateInventoryData(inventory, displayName));
    }
}
