using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[NpcTool]
[Preserve]
public sealed class MoveNpcTool : NpcTool<MoveArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{""type"": ""object"",
          ""properties"": {
            ""targetLandmark"": {
              ""type"": ""string"",
              ""minLength"": 1,
              ""description"": ""game_scene_get_npc_targets 返回的 targetLandmark""
            }
          },
          ""required"": [""targetLandmark""],
          ""additionalProperties"": false
        }");

    public override string Name => "game_npc_move";

    public override string Description =>
        "使 NPC 前往场景中的指定目标附近，并在目标外一定距离停下，避免与目标重叠。" +
        "targetLandmark 必须来自 game_scene_get_npc_targets 的查询结果；" +
        "目标列表不确定时应先调用查询工具。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, MoveArgs args)
    {
        context.Npc.MoveToLandmark(args);
        return ToolExecutionResult.Pending($"NPC 正在前往 {args.targetLandmark}。");
    }
}
