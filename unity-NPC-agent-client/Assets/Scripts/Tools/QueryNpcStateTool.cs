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

    public override string Description =>
        "查询当前对话 NPC 的实时运行状态、世界坐标和移动信息。" +
        "用于确认 NPC 当前是否空闲、是否位于 NavMesh、正在前往哪个目标以及剩余距离。";

    protected override AgentToolResult ExecuteCore(
        AgentToolContext context,
        NpcEntity npc,
        QueryNpcStateArgs args)
    {
        return Success(
            npc.CreateRuntimeStateData(),
            $"已获取 NPC '{npc.npcId}' 的当前状态。");
    }
}
