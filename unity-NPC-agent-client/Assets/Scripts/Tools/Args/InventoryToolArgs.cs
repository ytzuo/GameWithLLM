using System;

[Serializable]
public sealed class EmptyInventoryToolArgs : ToolArgsBase
{
    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[Serializable]
public sealed class ContainerInventoryArgs : ToolArgsBase
{
    public string containerId;

    public override bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            errorMessage = "containerId 不能为空";
            return false;
        }

        errorMessage = null;
        return true;
    }
}

[Serializable]
public sealed class PutItemInContainerArgs : ToolArgsBase
{
    public string containerId;
    public string itemId;
    public int quantity;

    public override bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            errorMessage = "containerId 不能为空";
            return false;
        }
        if (string.IsNullOrWhiteSpace(itemId))
        {
            errorMessage = "itemId 不能为空";
            return false;
        }
        if (quantity <= 0)
        {
            errorMessage = "quantity 必须大于 0";
            return false;
        }

        errorMessage = null;
        return true;
    }
}
