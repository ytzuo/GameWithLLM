using System.Collections.Generic;
using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class GetNearbyContainersTool : InventoryNpcTool<NearbyContainersArgs>
{
    public override string Name => "game_inventory_get_nearby_containers";
    public override string Description =>
        "查询场景中除 NPC 自身背包外的已注册容器，返回稳定 containerId、所属 targetId、距离和是否在交互范围内。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        NearbyContainersArgs args)
    {
        List<NearbyInventoryContainer> containers =
            InventoryToolSupport.GetContainers(npc, args.maxDistance, args.inRangeOnly);
        string message = containers.Count == 0
            ? ClientTextCatalogs.Message(
                "tool.inventory.no_containers",
                "当前筛选条件下没有可用容器。")
            : ClientTextCatalogs.Message(
                "tool.inventory.containers_found",
                "发现 {0} 个容器；操作前请使用返回的 containerId，并根据 inRange 判断是否需要移动。",
                containers.Count);
        return Success(InventoryToolSupport.CreateNearbyContainersData(containers), message);
    }
}
