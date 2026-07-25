using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

internal sealed class NpcTargetRecord
{
    public string TargetId { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public GameObject GameObject { get; }
    public bool IsDynamic { get; }

    public NpcTargetRecord(string targetId, string displayName, string category, GameObject gameObject, bool isDynamic)
    {
        TargetId = targetId;
        DisplayName = displayName;
        Category = category;
        GameObject = gameObject;
        IsDynamic = isDynamic;
    }
}

internal static class NpcTargetSupport
{
    private const float NavMeshSampleRadius = 2f;

    public static List<NpcTargetRecord> FindTargets()
    {
        var targets = new List<NpcTargetRecord>();
        NpcEntity[] npcs = UnityEngine.Object.FindObjectsByType<NpcEntity>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < npcs.Length; i++)
        {
            NpcEntity npc = npcs[i];
            if (npc == null || string.IsNullOrWhiteSpace(npc.npcId))
                continue;
            targets.Add(new NpcTargetRecord(AddPrefix(npc.npcId, "npc"), npc.npcId.Trim(), "npc", npc.gameObject, true));
        }

        PlayerMock[] players = UnityEngine.Object.FindObjectsByType<PlayerMock>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < players.Length; i++)
        {
            PlayerMock player = players[i];
            if (player == null || string.IsNullOrWhiteSpace(player.WorldTargetId))
                continue;
            targets.Add(new NpcTargetRecord(player.WorldTargetId, "玩家", "player", player.gameObject, true));
        }

        NpcLandmark[] landmarks = UnityEngine.Object.FindObjectsByType<NpcLandmark>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        for (int i = 0; i < landmarks.Length; i++)
        {
            NpcLandmark landmark = landmarks[i];
            if (landmark == null || string.IsNullOrWhiteSpace(landmark.TargetId))
                continue;
            targets.Add(new NpcTargetRecord(landmark.TargetId, landmark.DisplayName, "landmark", landmark.gameObject, false));
        }

        targets.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.TargetId, right.TargetId));
        for (int i = 1; i < targets.Count; i++)
        {
            if (string.Equals(targets[i - 1].TargetId, targets[i].TargetId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolExecutionException(
                    "TARGET_ID_AMBIGUOUS",
                    $"场景中存在多个 targetId 为 '{targets[i].TargetId}' 的目标，请配置唯一标识。");
            }
        }
        return targets;
    }

    public static NpcTargetRecord ResolveUniqueTarget(string targetId)
    {
        List<NpcTargetRecord> targets = FindTargets();
        for (int i = 0; i < targets.Count; i++)
        {
            if (string.Equals(targets[i].TargetId, targetId, StringComparison.OrdinalIgnoreCase))
                return targets[i];
        }
        throw new ToolExecutionException(
            "TARGET_NOT_FOUND",
            $"场景中不存在 targetId 为 '{targetId}' 的激活目标。请先调用 game_scene_get_targets。");
    }

    public static NpcTargetRecord FindOwnerTarget(GameObject gameObject)
    {
        if (gameObject == null)
            return null;
        List<NpcTargetRecord> targets = FindTargets();
        for (int i = 0; i < targets.Count; i++)
        {
            if (gameObject == targets[i].GameObject || gameObject.transform.IsChildOf(targets[i].GameObject.transform))
                return targets[i];
        }
        return null;
    }

    public static bool TryCalculatePath(NpcEntity npc, NpcTargetRecord target, out float pathDistance)
    {
        pathDistance = 0f;
        if (npc == null || !npc.IsOnNavMesh || target?.GameObject == null ||
            !NavMesh.SamplePosition(npc.transform.position, out NavMeshHit start, NavMeshSampleRadius, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(target.GameObject.transform.position, out NavMeshHit end, NavMeshSampleRadius, NavMesh.AllAreas))
        {
            return false;
        }

        var path = new NavMeshPath();
        if (!NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) ||
            path.status != NavMeshPathStatus.PathComplete || path.corners == null || path.corners.Length == 0)
        {
            return false;
        }

        for (int i = 1; i < path.corners.Length; i++)
            pathDistance += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return true;
    }

    private static string AddPrefix(string value, string prefix)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        if (normalized.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase))
            return normalized;
        return string.IsNullOrEmpty(normalized) ? string.Empty : $"{prefix}:{normalized}";
    }
}