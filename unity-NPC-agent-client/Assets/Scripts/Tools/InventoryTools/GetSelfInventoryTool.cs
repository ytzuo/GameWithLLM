using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class GetSelfInventoryTool : InventoryNpcTool<EmptyInventoryToolArgs>
{
    public override string Name => "game_inventory_get_self";

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
