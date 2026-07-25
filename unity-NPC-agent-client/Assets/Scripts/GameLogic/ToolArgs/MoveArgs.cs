using System;

[Serializable]
public class MoveArgs : ToolArgsBase
{
    public string targetId;
    public float approachDistance;

    public override bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            errorMessage = "targetId 不能为空";
            return false;
        }
        if (approachDistance < 0f || approachDistance > 10f)
        {
            errorMessage = "approachDistance 必须在 0 到 10 米之间；0 表示使用 NPC 默认值";
            return false;
        }
        errorMessage = null;
        return true;
    }
}