using UnityEngine;

/// <summary>
/// 物品的静态数据定义，通过 ScriptableObject 在编辑器中配置。
/// </summary>
[System.Serializable]
public class ItemData
{
    /// <summary>物品唯一标识符。</summary>
    public string ItemId;

    /// <summary>物品显示名称。</summary>
    public string ItemName;

    /// <summary>物品图标。</summary>
    public Sprite Icon;

    /// <summary>物品描述文本。</summary>
    public string Description;

    /// <summary>单个格子最大堆叠数量。</summary>
    public int MaxStackSize = 99;
}
