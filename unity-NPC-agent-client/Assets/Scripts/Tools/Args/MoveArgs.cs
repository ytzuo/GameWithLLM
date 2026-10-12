using System;

[Serializable]
public class MoveArgs : ToolArgsBase
{
    [ToolParameter(
        Required = true,
        MinLength = 1)]
    public string targetId;

    [ToolParameter(
        Minimum = 0,
        Maximum = 10)]
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
