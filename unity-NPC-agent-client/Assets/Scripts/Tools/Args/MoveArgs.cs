using System;
using UnityEngine;

[Serializable]
public class MoveArgs : ToolArgsBase
{
    public string targetLandmark;

    // 在这里写你的合法性检查，专注于防止游戏崩溃
    public override bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(targetLandmark))
        {
            errorMessage = "targetLandmark 不能为空";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
