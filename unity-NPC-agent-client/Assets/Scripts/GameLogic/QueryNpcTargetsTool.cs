using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

[Serializable]
public sealed class QuerySceneTargetsArgs : ToolArgsBase
{
    public string[] targetIds;
    public string[] categories;
    public float maxDistance;
    public bool reachableOnly;

    public override bool Validate(out string errorMessage)
    {
        if (maxDistance < 0f)
        {
            errorMessage = "maxDistance 不能小于 0";
            return false;
        }
        string[] allowed = { "npc", "player", "landmark" };
        if (categories != null)
        {
            for (int i = 0; i < categories.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(categories[i]) ||
                    !allowed.Contains(categories[i], StringComparer.OrdinalIgnoreCase))
                {
                    errorMessage = $"不支持的目标类别：{categories[i]}";
                    return false;
                }
            }
        }
        errorMessage = null;
        return true;
    }
}

[NpcTool]
[Preserve]
public sealed class QuerySceneTargetsTool : NpcTool<QuerySceneTargetsArgs>
{
    private static readonly JObject Schema = JObject.Parse(
        @"{
          ""type"": ""object"",
          ""properties"": {
            ""targetIds"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""minLength"": 1 }, ""uniqueItems"": true },
            ""categories"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""enum"": [""npc"", ""player"", ""landmark""] }, ""uniqueItems"": true },
            ""maxDistance"": { ""type"": ""number"", ""minimum"": 0, ""description"": ""最大直线距离；0 或省略表示不限制"" },
            ""reachableOnly"": { ""type"": ""boolean"", ""description"": ""是否只返回具有完整 NavMesh 路径的目标"" }
          },
          ""additionalProperties"": false
        }");

    public override string Name => "game_scene_get_targets";

    public override string Description =>
        "查询当前场景中的其他 NPC、玩家和地标，返回稳定 targetId、类别、距离和 NavMesh 可达性。" +
        "game_npc_move 必须使用本工具返回的 targetId。";

    public override JObject InputSchema => (JObject)Schema.DeepClone();

    protected override ToolExecutionResult ExecuteCore(NpcToolContext context, QuerySceneTargetsArgs args)
    {
        Vector3 origin = context.Npc.transform.position;
        HashSet<string> requestedIds = CreateSet(args.targetIds);
        HashSet<string> requestedCategories = CreateSet(args.categories);

        List<TargetSummary> targets = NpcTargetSupport.FindTargets()
            .Where(target => target.GameObject != context.Npc.gameObject)
            .Where(target => requestedIds == null || requestedIds.Contains(target.TargetId))
            .Where(target => requestedCategories == null || requestedCategories.Contains(target.Category))
            .Select(target => CreateSummary(context.Npc, origin, target))
            .Where(target => args.maxDistance <= 0f || target.Distance <= args.maxDistance)
            .Where(target => !args.reachableOnly || target.IsReachable)
            .OrderBy(target => target.Distance)
            .ThenBy(target => target.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string message = targets.Count == 0
            ? "当前筛选条件下没有可用移动目标。"
            : $"当前可用移动目标有：{string.Join("、", targets.Select(target => target.DisplayName))}。" +
              "移动时请使用返回结果中的 targetId。";

        return ToolExecutionResult.Success(JToken.FromObject(new { count = targets.Count, targets }), message);
    }

    private static HashSet<string> CreateSet(string[] values)
    {
        if (values == null || values.Length == 0)
            return null;
        return new HashSet<string>(values.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
    }

    private static TargetSummary CreateSummary(NpcEntity npc, Vector3 origin, NpcTargetRecord target)
    {
        bool isReachable = NpcTargetSupport.TryCalculatePath(npc, target, out float pathDistance);
        return new TargetSummary
        {
            TargetId = target.TargetId,
            DisplayName = target.DisplayName,
            Category = target.Category,
            Distance = Math.Round(Vector3.Distance(origin, target.GameObject.transform.position), 2),
            IsReachable = isReachable,
            PathDistance = isReachable ? (double?)Math.Round(pathDistance, 2) : null,
            IsDynamic = target.IsDynamic
        };
    }

    private sealed class TargetSummary
    {
        [JsonProperty("targetId")] public string TargetId { get; set; }
        [JsonProperty("displayName")] public string DisplayName { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("distance")] public double Distance { get; set; }
        [JsonProperty("isReachable")] public bool IsReachable { get; set; }
        [JsonProperty("pathDistance")] public double? PathDistance { get; set; }
        [JsonProperty("isDynamic")] public bool IsDynamic { get; set; }
    }
}