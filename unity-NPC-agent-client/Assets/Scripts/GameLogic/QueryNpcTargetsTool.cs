using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
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
        "查询当前场景中可供 NPC 接近的地点、其他 NPC 和玩家。" +
        "返回稳定 targetLandmark、显示名称、类别、距离和 NavMesh 可达性；" +
        "game_npc_move 的 targetLandmark 必须使用查询结果中的同名字段。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, QueryNpcTargetsArgs args)
    {
        Vector3 origin = context.Npc.transform.position;
        List<TargetSummary> targets = NpcTargetSupport.FindTargets()
            .Where(target => target != context.Npc.gameObject)
            .Select(target => CreateSummary(context.Npc, origin, target))
            .OrderBy(target => target.Distance)
            .ThenBy(target => target.TargetLandmark, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string message = targets.Count == 0
            ? "当前场景没有可用的 NPC 移动目标。"
            : $"当前可用移动目标有：{string.Join("、", targets.Select(target => target.DisplayName))}。" +
              "移动时请使用返回结果中的 targetLandmark。";

        return ToolExecutionResult.Success(
            JToken.FromObject(new
            {
                count = targets.Count,
                targets
            }),
            message);
    }

    private static TargetSummary CreateSummary(NpcEntity npc, Vector3 origin, GameObject target)
    {
        bool isReachable = NpcTargetSupport.TryCalculatePath(npc, target, out float pathDistance);
        NpcEntity targetNpc = target.GetComponent<NpcEntity>();
        bool isPlayer = target.GetComponent<PlayerMock>() != null;
        return new TargetSummary
        {
            TargetLandmark = target.name,
            DisplayName = isPlayer ? "玩家" : targetNpc != null ? targetNpc.npcId : target.name,
            Category = isPlayer ? "player" : targetNpc != null ? "npc" : "landmark",
            Distance = Math.Round(Vector3.Distance(origin, target.transform.position), 2),
            IsReachable = isReachable,
            PathDistance = isReachable ? (double?)Math.Round(pathDistance, 2) : null
        };
    }

    private sealed class TargetSummary
    {
        [JsonProperty("targetLandmark")] public string TargetLandmark { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("distance")] public double Distance { get; set; }
        [JsonProperty("isReachable")] public bool IsReachable { get; set; }
        [JsonProperty("pathDistance")] public double? PathDistance { get; set; }
    }
}
