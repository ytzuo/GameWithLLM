using System.Collections.Generic;
using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class GetNearbyContainersTool : InventoryNpcTool<NearbyContainersArgs>
{
    public override string Name => "game_inventory_get_nearby_containers";
    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        NearbyContainersArgs args)
    {
        List<NearbyInventoryContainer> containers =
            InventoryToolSupport.GetContainers(npc, args.maxDistance, args.inRangeOnly);
        string message = containers.Count == 0
            ? ClientTextCatalogs.Message("tool.inventory.no_containers")
            : ClientTextCatalogs.Message(
                "tool.inventory.containers_found",
                containers.Count);
        return Success(InventoryToolSupport.CreateNearbyContainersData(containers), message);
    }
}
