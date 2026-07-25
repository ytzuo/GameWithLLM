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
    [ToolParameter(
        UniqueItems = true,
        ItemMinLength = 1 )]
    public string[] targetIds;

    [ToolParameter(
        UniqueItems = true,
        ItemAllowedValues = new string[] { "npc", "player", "landmark" })]
    public string[] categories;

    [ToolParameter(
        Minimum = 0,
        Description = "最大直线距离；0 或省略表示不限制")]
    public float maxDistance;

    [ToolParameter(Description = "是否只返回具有完整 NavMesh 路径的目标")]
    public bool reachableOnly;

    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[NpcTool]
[Preserve]
public sealed class QuerySceneTargetsTool : NpcTool<QuerySceneTargetsArgs>
{
    public override string Name => "game_scene_get_targets";

    public override string Description =>
        "查询当前场景中的其他 NPC、玩家和地标，返回稳定 targetId、类别、距离和 NavMesh 可达性。" +
        "game_npc_move 必须使用本工具返回的 targetId。";

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
