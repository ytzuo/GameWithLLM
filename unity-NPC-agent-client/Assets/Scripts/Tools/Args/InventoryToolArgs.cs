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
    [ToolParameter(
        Required = true,
        MinLength = 1)]
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
public sealed class NearbyContainersArgs : ToolArgsBase
{
    [ToolParameter(Minimum = 0)]
    public float maxDistance;

    [ToolParameter]
    public bool inRangeOnly;

    public override bool Validate(out string errorMessage)
    {
        errorMessage = null;
        return true;
    }
}

[Serializable]
public sealed class PutItemInContainerArgs : ToolArgsBase
{
    [ToolParameter(
        Required = true,
        MinLength = 1)]
    public string containerId;

    [ToolParameter(
        Required = true,
        MinLength = 1)]
    public string itemId;

    [ToolParameter(Required = true, Minimum = 1)]
    public int quantity;

    public override bool Validate(out string errorMessage) =>
        InventoryTransferArgsValidation.Validate(containerId, itemId, quantity, out errorMessage);
}

[Serializable]
public sealed class TakeItemFromContainerArgs : ToolArgsBase
{
    [ToolParameter(
        Required = true,
        MinLength = 1)]
    public string containerId;

    [ToolParameter(
        Required = true,
        MinLength = 1)]
    public string itemId;

    [ToolParameter(Required = true, Minimum = 1)]
    public int quantity;

    public override bool Validate(out string errorMessage) =>
        InventoryTransferArgsValidation.Validate(containerId, itemId, quantity, out errorMessage);
}

internal static class InventoryTransferArgsValidation
{
    public static bool Validate(string containerId, string itemId, int quantity, out string errorMessage)
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
        errorMessage = null;
        return true;
    }
}
