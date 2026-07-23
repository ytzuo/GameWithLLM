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
              ""description"": ""game_scene_get_npc_targets 返回的目标名称""
            }
          },
          ""required"": [""targetLandmark""],
          ""additionalProperties"": false
        }");

    public override string Name => "game_npc_move";

    public override string Description =>
        "使 NPC 前往场景中的指定目标。targetLandmark 必须来自 " +
        "game_scene_get_npc_targets 的查询结果；目标列表不确定时应先调用查询工具。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override string ExecuteCore(NpcToolContext context, MoveArgs args)
    {
        return context.Npc.MoveToLandmark(args);
    }
}
