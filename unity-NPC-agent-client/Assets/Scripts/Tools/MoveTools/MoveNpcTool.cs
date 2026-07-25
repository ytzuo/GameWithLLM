using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class MoveNpcTool : NpcTool<MoveArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{""type"": ""object"",
          ""properties"": {
            ""targetId"": {
              ""type"": ""string"",
              ""minLength"": 1,
              ""description"": ""game_scene_get_targets 返回的稳定 targetId""
            },
            ""approachDistance"": {
              ""type"": ""number"",
              ""minimum"": 0,
              ""maximum"": 10,
              ""description"": ""与目标保持的距离；0 或省略时使用 NPC 默认停止距离""
            }
          },
          ""required"": [""targetId""],
          ""additionalProperties"": false
        }");

    public override string Name => "game_npc_move";

    public override string Description =>
        "使 NPC 前往 game_scene_get_targets 返回的目标附近。" +
        "NPC 和玩家属于动态目标，执行期间会持续更新路径；targetId 不确定时应先查询目标。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    public override bool IsAvailable(NpcToolContext context) =>
        context?.Npc != null && context.Npc.GetComponent<UnityEngine.AI.NavMeshAgent>() != null;

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, MoveArgs args)
    {
        context.Npc.MoveToTarget(args);
        return ToolExecutionResult.Pending($"NPC 正在前往 {args.targetId}。");
    }
}