using System;
using UnityEngine;

[Serializable]
public class MoveArgs : ToolArgsBase
{
    public string targetLandmark;

    // 在这里写你的合法性检查，专注于防止游戏崩溃
    public override bool Validate(out string errorMessage)
    {
        if (string.IsNullOrEmpty(targetLandmark)) { errorMessage = "targetLandmark 不能为空"; return false; }

        // 甚至可以检查游戏逻辑：防止大模型瞎编一个不存在的地标
        if (targetLandmark != "warehouse" && targetLandmark != "gate")
        {
            errorMessage = $"游戏场景中不存在名为 '{targetLandmark}' 的安全地标。";
            return false;
        }

        errorMessage = null;
        return true;
    }
}