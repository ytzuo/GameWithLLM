using System;
using GameWithLLM.AgentRuntime;
using UnityEngine.Scripting;

[Serializable]
public sealed class QueryNpcStateArgs : ToolArgsBase
{
    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[AgentTool]
[Preserve]
public sealed class QueryNpcStateTool : NpcTool<QueryNpcStateArgs>
{
    public override string Name => "game_npc_get_state";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        QueryNpcStateArgs args)
    {
        return Success(
            npc.CreateRuntimeStateData(),
            ClientTextCatalogs.Message(
                "tool.state.loaded",
                npc.npcId));
    }
}
