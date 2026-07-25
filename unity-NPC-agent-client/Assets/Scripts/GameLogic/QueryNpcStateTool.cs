using System;
using Newtonsoft.Json.Linq;
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

[NpcTool]
[Preserve]
public sealed class QueryNpcStateTool : NpcTool<QueryNpcStateArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {},
          ""additionalProperties"": false
        }");

    public override string Name => "game_npc_get_state";

    public override string Description =>
        "查询当前对话 NPC 的实时运行状态、世界坐标和移动信息。" +
        "用于确认 NPC 当前是否空闲、是否位于 NavMesh、正在前往哪个目标以及剩余距离。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(
        NpcToolContext context,
        QueryNpcStateArgs args)
    {
        return ToolExecutionResult.Success(
            context.Npc.CreateRuntimeStateData(),
            $"已获取 NPC '{context.Npc.npcId}' 的当前状态。");
    }
}
