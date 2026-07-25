using System;

[Serializable]
public class MoveArgs : ToolArgsBase
{
    [ToolParameter(
        Required = true,
        MinLength = 1,
        Description = "game_scene_get_targets 返回的稳定 targetId")]
    public string targetId;

    [ToolParameter(
        Minimum = 0,
        Maximum = 10,
        Description = "与目标保持的距离；0 或省略时使用 NPC 默认停止距离")]
    public float approachDistance;

    public override bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            errorMessage = "targetId 不能为空";
            return false;
        }
        errorMessage = null;
        return true;
    }
}
