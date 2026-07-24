using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

[Serializable]
public sealed class QueryNpcTargetsArgs : ToolArgsBase
{
    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[NpcTool]
[Preserve]
public sealed class QueryNpcTargetsTool : NpcTool<QueryNpcTargetsArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {},
          ""additionalProperties"": false
        }");

    public override string Name => "game_scene_get_npc_targets";

    public override string Description =>
        "查询当前已加载场景中所有激活且带有 npcTarget 标签的移动目标。" +
        "返回的 targets 名称可作为 game_npc_move 的 targetLandmark。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, QueryNpcTargetsArgs args)
    {
        string[] targetNames = NpcTargetSupport.FindTargets()
            .Select(target => target.name)
            .ToArray();

        return ToolExecutionResult.Success(JToken.FromObject(new
        {
            count = targetNames.Length,
            targets = targetNames
        }));
    }
}
