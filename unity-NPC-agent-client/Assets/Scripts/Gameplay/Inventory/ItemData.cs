using UnityEngine;

/// <summary>
/// 物品的静态数据定义，通过 ScriptableObject 在编辑器中配置。
/// </summary>
[System.Serializable]
public class ItemData
{
    /// <summary>物品唯一标识符。</summary>
    public string ItemId;

    /// <summary>物品显示名称的稳定文本键。</summary>
    public string DisplayNameKey;

    /// <summary>物品描述的稳定文本键。</summary>
    public string DescriptionKey;

    /// <summary>所属图标族 SpriteAtlas 的稳定 Addressables 地址。</summary>
    public string IconAtlasAddress;

    /// <summary>SpriteAtlas 中的导入 Sprite 名。</summary>
    public string IconName;

    /// <summary>可选的世界表现 Prefab 地址；不随图标预加载。</summary>
    public string WorldVisualAddress;

    /// <summary>单个格子最大堆叠数量。</summary>
    public int MaxStackSize = 99;

    [System.NonSerialized] private Sprite _icon;
    [System.NonSerialized] private string _itemName;
    [System.NonSerialized] private string _description;

    public string ItemName => _itemName ?? DisplayNameKey;
    public string Description => _description ?? DescriptionKey;
    public Sprite Icon => _icon;

    internal void SetLoadedIcon(Sprite icon) => _icon = icon;

    internal void SetLoadedPresentation(string itemName, string description)
    {
        _itemName = itemName;
        _description = description;
    }
}
