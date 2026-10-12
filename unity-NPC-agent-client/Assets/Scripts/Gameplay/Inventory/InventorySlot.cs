using System;

/// <summary>
/// 运行时物品栏格子，保存物品引用及其当前数量。
/// </summary>
[Serializable]
public class InventorySlot
{
    // ── Fields ──

    /// <summary>格子中的物品数据。</summary>
    public ItemData Item;

    /// <summary>当前持有数量。</summary>
    public int Quantity;

    // ── Properties ──

    /// <summary>格子是否为空。</summary>
    public bool IsEmpty => Item == null || Quantity <= 0;

    /// <summary>格子是否已满（达到最大堆叠数）。</summary>
    public bool IsFull => Item != null && Quantity >= Item.MaxStackSize;

    // ── Query ──

    /// <summary>
    /// 检查格子是否可以再容纳指定数量的物品。
    /// </summary>
    /// <param name="amount">要尝试添加的数量。</param>
    /// <returns>是否可以添加。</returns>
    public bool CanAdd(int amount)
    {
        if (amount <= 0) return false;
        if (IsEmpty) return true;
        return Item != null && Quantity + amount <= Item.MaxStackSize;
    }

    /// <summary>
    /// 检查格子是否有足够数量可以被移除。
    /// </summary>
    /// <param name="amount">要尝试移除的数量。</param>
    /// <returns>是否可以移除。</returns>
    public bool CanRemove(int amount)
    {
        if (amount <= 0) return false;
        if (IsEmpty) return false;
        return Quantity >= amount;
    }

    /// <summary>
    /// 返回该格子还能容纳多少当前物品。
    /// </summary>
    /// <returns>剩余可堆叠空间。如果格子为空则返回 0。</returns>
    public int AvailableSpace()
    {
        if (Item == null) return 0;
        return Item.MaxStackSize - Quantity;
    }
}
