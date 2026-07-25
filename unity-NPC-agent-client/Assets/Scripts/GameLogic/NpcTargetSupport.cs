using System;
using UnityEngine;
using UnityEngine.AI;

internal static class NpcTargetSupport
{
    public const string TagName = "npcTarget";
    private const float NavMeshSampleRadius = 2f;

    public static GameObject[] FindTargets()
    {
        GameObject[] targets;
        try
        {
            targets = GameObject.FindGameObjectsWithTag(TagName);
        }
        catch (UnityException ex)
        {
            throw new ToolExecutionException(
                "NPC_TARGET_TAG_UNDEFINED",
                $"项目未定义 Unity 标签 '{TagName}'：{ex.Message}");
        }

        Array.Sort(
            targets,
            (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.name, right.name));
        for (int i = 1; i < targets.Length; i++)
        {
            if (string.Equals(targets[i - 1].name, targets[i].name, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolExecutionException(
                    "NPC_TARGET_AMBIGUOUS",
                    $"场景中存在多个名为 '{targets[i].name}' 的移动目标，请为目标设置唯一名称。");
            }
        }
        return targets;
    }

    public static GameObject ResolveUniqueTarget(string targetName)
    {
        GameObject[] targets = FindTargets();
        for (int i = 0; i < targets.Length; i++)
        {
            if (string.Equals(targets[i].name, targetName, StringComparison.OrdinalIgnoreCase))
                return targets[i];
        }

        throw new ToolExecutionException(
            "NPC_TARGET_NOT_FOUND",
            $"场景中不存在名为 '{targetName}' 且带有 '{TagName}' 标签的激活移动目标。请先查询可用目标。");
    }

    public static bool TryCalculatePath(
        NpcEntity npc,
        GameObject target,
        out float pathDistance)
    {
        pathDistance = 0f;
        if (npc == null || !npc.IsOnNavMesh || target == null ||
            !NavMesh.SamplePosition(npc.transform.position, out NavMeshHit start, NavMeshSampleRadius, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(target.transform.position, out NavMeshHit end, NavMeshSampleRadius, NavMesh.AllAreas))
        {
            return false;
        }

        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete ||
            path.corners == null ||
            path.corners.Length == 0)
        {
            return false;
        }

        for (int i = 1; i < path.corners.Length; i++)
            pathDistance += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return true;
    }
}
