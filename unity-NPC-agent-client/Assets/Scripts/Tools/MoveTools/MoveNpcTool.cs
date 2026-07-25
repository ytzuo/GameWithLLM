using UnityEngine;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class MoveNpcTool : NpcTool<MoveArgs>
{
    public override string Name => "game_npc_move";

    public override string Description =>
        "使 NPC 前往 game_scene_get_targets 返回的目标附近。" +
        "NPC 和玩家属于动态目标，执行期间会持续更新路径；targetId 不确定时应先查询目标。";

    public override bool IsAvailable(NpcToolContext context)
    {
        if (context?.Npc == null)
            return false;
        var agent = context.Npc.GetComponent<UnityEngine.AI.NavMeshAgent>();
        return agent != null && agent.enabled && agent.gameObject.activeInHierarchy;
    }

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, MoveArgs args)
    {
        context.Npc.MoveToTarget(args);
        return ToolExecutionResult.Pending($"NPC 正在前往 {args.targetId}。");
    }
}
