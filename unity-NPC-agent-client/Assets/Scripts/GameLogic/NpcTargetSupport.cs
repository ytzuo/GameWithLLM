using System;
using UnityEngine;

internal static class NpcTargetSupport
{
    public const string TagName = "npcTarget";

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
        return targets;
    }

    public static GameObject ResolveUniqueTarget(string targetName)
    {
        GameObject match = null;
        GameObject[] targets = FindTargets();

        for (int i = 0; i < targets.Length; i++)
        {
            GameObject candidate = targets[i];
            if (!string.Equals(candidate.name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (match != null && match != candidate)
            {
                throw new ToolExecutionException(
                    "NPC_TARGET_AMBIGUOUS",
                    $"场景中存在多个名为 '{targetName}' 且带有 '{TagName}' 标签的目标，请为目标设置唯一名称。");
            }
            match = candidate;
        }

        if (match == null)
        {
            throw new ToolExecutionException(
                "NPC_TARGET_NOT_FOUND",
                $"场景中不存在名为 '{targetName}' 且带有 '{TagName}' 标签的激活目标。请先查询可用目标。");
        }

        return match;
    }
}
