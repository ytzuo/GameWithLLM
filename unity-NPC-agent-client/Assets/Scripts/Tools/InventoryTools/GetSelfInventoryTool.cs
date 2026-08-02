using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class GetSelfInventoryTool : InventoryNpcTool<EmptyInventoryToolArgs>
{
    public override string Name => "game_inventory_get_self";

    public override string Description =>
        "获取当前 NPC 自身背包中的全部物品及数量。只查询当前对话 NPC，不查询其他容器。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        EmptyInventoryToolArgs args)
    {
        InventoryComponent inventory = InventoryToolSupport.RequireNpcInventory(npc);
        string displayName = InventoryViewModel.Instance.GetContainerName(inventory);
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = npc.gameObject.name;

        return Success(InventoryToolSupport.CreateInventoryData(inventory, displayName));
    }
}
