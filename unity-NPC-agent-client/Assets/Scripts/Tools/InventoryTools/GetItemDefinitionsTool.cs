using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class GetItemDefinitionsTool : NpcTool<EmptyInventoryToolArgs>
{
    public override string Name => "game_inventory_get_item_definitions";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        EmptyInventoryToolArgs args)
    {
        return Success(InventoryToolSupport.CreateItemDefinitionsData(
            InventoryToolSupport.RequireItemCatalog()));
    }
}
