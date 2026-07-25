using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class GetContainerInventoryTool : InventoryNpcTool<ContainerInventoryArgs>
{
    public override string Name => "game_inventory_get_container";

    public override string Description =>
        "获取指定容器中的全部物品及数量。目标必须是已注册的 InventoryComponent，" +
        "且位于当前 NPC 的交互距离内；不能用此工具查询 NPC 自身背包。";

    protected override ToolExecutionResult ExecuteCore(
        NpcToolContext context,
        ContainerInventoryArgs args)
    {
        NearbyInventoryContainer container =
            InventoryToolSupport.RequireNearbyContainer(context, args.containerId);
        return ToolExecutionResult.Success(InventoryToolSupport.CreateInventoryData(
            container.Inventory,
            container.DisplayName,
            container.Distance));
    }
}
