using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;
using UnityEngine.Scripting;

[AgentTool]
[Preserve]
public sealed class MoveNpcTool : NpcTool<MoveArgs>
{
    public override string Name => "game_npc_move";

    public override string Description =>
        "使 NPC 前往 game_scene_get_targets 返回的目标附近。" +
        "NPC 和玩家属于动态目标，执行期间会持续更新路径；targetId 不确定时应先查询目标。";

    public override bool IsAvailable(AgentToolContext context)
    {
        if (!(context?.Entity is NpcEntity npc))
            return false;
        var agent = npc.GetComponent<UnityEngine.AI.NavMeshAgent>();
        return agent != null && agent.enabled && agent.gameObject.activeInHierarchy;
    }

    protected override ValueTask<AgentToolResult> ExecuteCoreAsync(
        AgentToolContext context,
        NpcEntity npc,
        MoveArgs args,
        CancellationToken cancellationToken) =>
        npc.MoveToTargetAsync(args, context, cancellationToken);
}
