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
        npc.MoveToTargetAsync(args.targetId, args.approachDistance, context, cancellationToken);
}
