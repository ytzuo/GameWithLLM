using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class GetContainerInventoryTool : InventoryNpcTool<ContainerInventoryArgs>
{
    public override string Name => "game_inventory_get_container";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        ContainerInventoryArgs args)
    {
        NearbyInventoryContainer container =
            InventoryToolSupport.RequireNearbyContainer(npc, args.containerId);
        return Success(InventoryToolSupport.CreateInventoryData(
            container.Inventory,
            container.DisplayName,
            container.Distance));
    }
}
