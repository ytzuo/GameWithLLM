using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class GetItemDefinitionsTool : NpcTool<EmptyInventoryToolArgs>
{
    public override string Name => "game_inventory_get_item_definitions";

    public override string Description =>
        "获取当前游戏定义的全部物品种类及其 itemId、名称、描述和最大堆叠数量。" +
        "查询物品标识时使用此工具，不会读取任何运行时背包。";

    protected override ToolExecutionResult ExecuteCore(
        NpcToolContext context,
        EmptyInventoryToolArgs args)
    {
        return ToolExecutionResult.Success(InventoryToolSupport.CreateItemDefinitionsData(
            InventoryToolSupport.RequireItemCatalog()));
    }
}
