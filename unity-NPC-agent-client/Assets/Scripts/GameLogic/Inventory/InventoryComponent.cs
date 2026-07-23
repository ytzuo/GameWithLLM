using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 物品栏组件。挂载到任意 GameObject 上即可使其成为物品容器。
/// </summary>
public class InventoryComponent : MonoBehaviour
{
    // ── Fields ──

    [SerializeField] private List<InventorySlot> _slots = new List<InventorySlot>();
    [SerializeField] private int _maxSlots = 21;

    // ── Properties ──

    /// <summary>物品栏最大格子数。</summary>
    public int MaxSlots { get; private set; }

    /// <summary>只读的格子列表。</summary>
    public IReadOnlyList<InventorySlot> Slots => _slots;

    /// <summary>已占用的格子数量（非空格子）。</summary>
    public int OccupiedSlotCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (!_slots[i].IsEmpty) count++;
            }
            return count;
        }
    }

    /// <summary>空余格子数量。</summary>
    public int EmptySlotCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].IsEmpty) count++;
            }
            return count;
        }
    }

    /// <summary>物品栏是否已满（没有空余格子）。</summary>
    public bool IsFull => EmptySlotCount <= 0;

    // ── Events ──

    /// <summary>物品栏任意格子变化时触发。</summary>
    public event Action OnInventoryChanged;

    /// <summary>指定索引的格子变化时触发。</summary>
    public event Action<int> OnSlotChanged;

    // ── Lifecycle ──

    private void Awake()
    {
        MaxSlots = _maxSlots;
        EnsureSlotsInitialized();
    }

    private void OnEnable()
    {
        string displayName = gameObject.name;
        InventoryViewModel.Instance.RegisterContainer(this, displayName);
    }

    private void OnDisable()
    {
        InventoryViewModel.Instance.UnregisterContainer(this);
    }

    /// <summary>
    /// Set a custom display name for this container (shown in inventory list UI).
    /// If not set, uses the GameObject name.
    /// </summary>
    public void SetDisplayName(string name)
    {
        // Re-register with new name
        InventoryViewModel.Instance.UnregisterContainer(this);
        InventoryViewModel.Instance.RegisterContainer(this, name);
    }

    // ── Query ──

    /// <summary>
    /// 统计物品栏中指定物品的总持有数量。
    /// </summary>
    /// <param name="item">要查询的物品数据。</param>
    /// <returns>该物品的总数量。</returns>
    public int GetItemCount(ItemData item)
    {
        if (item == null)
        {
            Debug.LogWarning("GetItemCount: item 参数为 null");
            return 0;
        }

        int total = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            InventorySlot slot = _slots[i];
            if (slot.Item == item && !slot.IsEmpty)
                total += slot.Quantity;
        }
        return total;
    }

    /// <summary>
    /// 检查是否持有足够数量的指定物品。
    /// </summary>
    /// <param name="item">要查询的物品数据。</param>
    /// <param name="quantity">需要的数量，默认为 1。</param>
    /// <returns>是否持有足够数量。</returns>
    public bool HasItem(ItemData item, int quantity = 1)
    {
        return GetItemCount(item) >= quantity;
    }

    /// <summary>
    /// 获取包含指定物品的第一个格子索引。
    /// </summary>
    /// <param name="item">要查找的物品数据。</param>
    /// <returns>格子索引，未找到则返回 -1。</returns>
    public int GetFirstSlotIndex(ItemData item)
    {
        if (item == null) return -1;

        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Item == item && !_slots[i].IsEmpty)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 安全获取指定索引的格子。
    /// </summary>
    /// <param name="index">格子索引。</param>
    /// <returns>对应的 InventorySlot，索引越界返回 null。</returns>
    public InventorySlot GetSlot(int index)
    {
        if (!IsValidIndex(index))
        {
            Debug.LogWarning($"GetSlot: 索引 {index} 越界，有效范围 0-{_slots.Count - 1}");
            return null;
        }
        return _slots[index];
    }

    /// <summary>
    /// 检查物品栏是否有足够空间容纳指定数量的物品。
    /// </summary>
    /// <param name="item">要检查的物品数据。</param>
    /// <param name="quantity">要添加的数量，默认为 1。</param>
    /// <returns>是否可以全部容纳。</returns>
    public bool CanAddItem(ItemData item, int quantity = 1)
    {
        return GetMaxAddableAmount(item) >= quantity;
    }

    /// <summary>
    /// 返回物品栏最多还能容纳多少个指定物品。
    /// </summary>
    /// <param name="item">要查询的物品数据。</param>
    /// <returns>最大可添加数量。</returns>
    public int GetMaxAddableAmount(ItemData item)
    {
        if (item == null) return 0;

        int totalSpace = 0;

        // 已有同种物品格子的剩余空间
        for (int i = 0; i < _slots.Count; i++)
        {
            InventorySlot slot = _slots[i];
            if (!slot.IsEmpty && slot.Item == item)
            {
                totalSpace += slot.AvailableSpace();
            }
        }

        // 空格子可以放入整堆
        for (int i = 0; i < _slots.Count; i++)
        {
            InventorySlot slot = _slots[i];
            if (slot.IsEmpty)
            {
                totalSpace += item.MaxStackSize;
            }
        }

        return totalSpace;
    }

    // ── Add ──

    /// <summary>
    /// 向物品栏添加物品。优先堆叠到已有的同种物品格子上，
    /// 剩余数量使用空格子。如果空间不足，部分物品会被丢弃。
    /// </summary>
    /// <param name="item">要添加的物品数据。</param>
    /// <param name="quantity">要添加的数量，默认为 1。</param>
    /// <returns>全部物品是否成功添加。</returns>
    public bool AddItem(ItemData item, int quantity = 1)
    {
        if (item == null)
        {
            Debug.LogWarning("AddItem: item 参数为 null");
            return false;
        }

        if (quantity <= 0)
        {
            Debug.LogWarning($"AddItem: quantity 必须大于 0，当前值为 {quantity}");
            return false;
        }

        int remaining = quantity;

        // ── 优先堆叠到已有的同种物品格子 ──
        for (int i = 0; i < _slots.Count && remaining > 0; i++)
        {
            InventorySlot slot = _slots[i];
            if (slot.Item == item && !slot.IsFull)
            {
                int canAdd = slot.AvailableSpace();
                int toAdd = Mathf.Min(canAdd, remaining);
                slot.Quantity += toAdd;
                remaining -= toAdd;
                NotifySlotChanged(i);
            }
        }

        // ── 剩余数量放入空格子 ──
        for (int i = 0; i < _slots.Count && remaining > 0; i++)
        {
            InventorySlot slot = _slots[i];
            if (slot.IsEmpty)
            {
                int toAdd = Mathf.Min(item.MaxStackSize, remaining);
                slot.Item = item;
                slot.Quantity = toAdd;
                remaining -= toAdd;
                NotifySlotChanged(i);
            }
        }

        if (remaining > 0)
        {
            Debug.LogWarning($"无法添加物品：{item.ItemName}，物品栏已满，{remaining} 个物品被丢弃");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 向指定格子添加物品。如果格子已有不同物品或已满则失败。
    /// </summary>
    /// <param name="slotIndex">目标格子索引。</param>
    /// <param name="item">要添加的物品数据。</param>
    /// <param name="quantity">要添加的数量。</param>
    /// <returns>是否添加成功。</returns>
    public bool AddItemToSlot(int slotIndex, ItemData item, int quantity)
    {
        if (item == null)
        {
            Debug.LogWarning("AddItemToSlot: item 参数为 null");
            return false;
        }

        if (quantity <= 0)
        {
            Debug.LogWarning($"AddItemToSlot: quantity 必须大于 0，当前值为 {quantity}");
            return false;
        }

        if (!IsValidIndex(slotIndex))
        {
            Debug.LogWarning($"AddItemToSlot: 索引 {slotIndex} 越界");
            return false;
        }

        InventorySlot slot = _slots[slotIndex];

        if (!slot.IsEmpty && slot.Item != item)
        {
            Debug.LogWarning($"AddItemToSlot: 格子 {slotIndex} 已有不同物品 '{slot.Item.ItemName}'，无法添加 '{item.ItemName}'");
            return false;
        }

        if (slot.IsFull)
        {
            Debug.LogWarning($"AddItemToSlot: 格子 {slotIndex} 已满，无法添加 '{item.ItemName}'");
            return false;
        }

        int canAdd = slot.IsEmpty ? item.MaxStackSize : slot.AvailableSpace();
        int toAdd = Mathf.Min(canAdd, quantity);

        if (slot.IsEmpty) slot.Item = item;
        slot.Quantity += toAdd;

        if (toAdd < quantity)
        {
            Debug.LogWarning($"AddItemToSlot: 格子空间不足，仅添加了 {toAdd}/{quantity} 个 '{item.ItemName}'");
        }

        NotifySlotChanged(slotIndex);
        return toAdd >= quantity;
    }

    // ── Remove ──

    /// <summary>
    /// 从物品栏中移除指定数量的物品。如果总量不足则不移除任何物品。
    /// 从第一个包含该物品的格子开始移除。
    /// </summary>
    /// <param name="item">要移除的物品数据。</param>
    /// <param name="quantity">要移除的数量，默认为 1。</param>
    /// <returns>是否移除成功。</returns>
    public bool RemoveItem(ItemData item, int quantity = 1)
    {
        if (item == null)
        {
            Debug.LogWarning("RemoveItem: item 参数为 null");
            return false;
        }

        if (quantity <= 0)
        {
            Debug.LogWarning($"RemoveItem: quantity 必须大于 0，当前值为 {quantity}");
            return false;
        }

        if (!HasItem(item, quantity))
        {
            Debug.LogWarning($"RemoveItem: '{item.ItemName}' 数量不足，需要 {quantity}，当前持有 {GetItemCount(item)}");
            return false;
        }

        int remaining = quantity;

        for (int i = 0; i < _slots.Count && remaining > 0; i++)
        {
            InventorySlot slot = _slots[i];
            if (slot.Item == item && !slot.IsEmpty)
            {
                int toRemove = Mathf.Min(slot.Quantity, remaining);
                slot.Quantity -= toRemove;
                remaining -= toRemove;

                if (slot.Quantity <= 0)
                {
                    slot.Item = null;
                    slot.Quantity = 0;
                }

                NotifySlotChanged(i);
            }
        }

        return true;
    }

    /// <summary>
    /// 从指定格子移除指定数量的物品。
    /// </summary>
    /// <param name="slotIndex">目标格子索引。</param>
    /// <param name="quantity">要移除的数量，默认为 1。</param>
    /// <returns>是否移除成功。</returns>
    public bool RemoveFromSlot(int slotIndex, int quantity = 1)
    {
        if (!IsValidIndex(slotIndex))
        {
            Debug.LogWarning($"RemoveFromSlot: 索引 {slotIndex} 越界");
            return false;
        }

        if (quantity <= 0)
        {
            Debug.LogWarning($"RemoveFromSlot: quantity 必须大于 0，当前值为 {quantity}");
            return false;
        }

        InventorySlot slot = _slots[slotIndex];

        if (!slot.CanRemove(quantity))
        {
            Debug.LogWarning($"RemoveFromSlot: 格子 {slotIndex} 数量不足，需要 {quantity}，当前持有 {slot.Quantity}");
            return false;
        }

        slot.Quantity -= quantity;

        if (slot.Quantity <= 0)
        {
            slot.Item = null;
            slot.Quantity = 0;
        }

        NotifySlotChanged(slotIndex);
        return true;
    }

    /// <summary>
    /// 清空指定格子。
    /// </summary>
    /// <param name="slotIndex">要清空的格子索引。</param>
    public void ClearSlot(int slotIndex)
    {
        if (!IsValidIndex(slotIndex))
        {
            Debug.LogWarning($"ClearSlot: 索引 {slotIndex} 越界");
            return;
        }

        // 如果格子已经是空的，无需操作
        if (_slots[slotIndex].IsEmpty)
            return;

        _slots[slotIndex].Item = null;
        _slots[slotIndex].Quantity = 0;
        NotifySlotChanged(slotIndex);
    }

    /// <summary>
    /// 清空所有格子，触发一次 OnInventoryChanged。
    /// </summary>
    public void ClearAll()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].Item = null;
            _slots[i].Quantity = 0;
        }
        NotifyInventoryChanged();
    }

    // ── Transfer ──

    /// <summary>
    /// 交换两个格子的内容。
    /// </summary>
    /// <param name="fromIndex">第一个格子索引。</param>
    /// <param name="toIndex">第二个格子索引。</param>
    public void SwapSlots(int fromIndex, int toIndex)
    {
        if (!IsValidIndex(fromIndex) || !IsValidIndex(toIndex))
        {
            Debug.LogWarning($"SwapSlots: 索引越界 fromIndex={fromIndex}, toIndex={toIndex}");
            return;
        }

        ItemData tempItem = _slots[fromIndex].Item;
        int tempQuantity = _slots[fromIndex].Quantity;

        _slots[fromIndex].Item = _slots[toIndex].Item;
        _slots[fromIndex].Quantity = _slots[toIndex].Quantity;

        _slots[toIndex].Item = tempItem;
        _slots[toIndex].Quantity = tempQuantity;

        NotifySlotChanged(fromIndex);
        NotifySlotChanged(toIndex);
    }

    /// <summary>
    /// 将物品从当前物品栏转移到目标物品栏。
    /// </summary>
    /// <param name="target">目标物品栏组件。</param>
    /// <param name="fromSlotIndex">源格子索引。</param>
    /// <param name="quantity">要转移的数量，默认为 1。</param>
    /// <returns>是否转移成功。</returns>
    public bool TransferTo(InventoryComponent target, int fromSlotIndex, int quantity = 1)
    {
        if (target == null)
        {
            Debug.LogWarning("TransferTo: target 参数为 null");
            return false;
        }

        if (target == this)
        {
            Debug.LogWarning("TransferTo: 不能转移到自身");
            return false;
        }

        if (!IsValidIndex(fromSlotIndex))
        {
            Debug.LogWarning($"TransferTo: 源格子索引 {fromSlotIndex} 越界");
            return false;
        }

        if (quantity <= 0)
        {
            Debug.LogWarning($"TransferTo: quantity 必须大于 0，当前值为 {quantity}");
            return false;
        }

        InventorySlot sourceSlot = _slots[fromSlotIndex];

        if (!sourceSlot.CanRemove(quantity))
        {
            Debug.LogWarning($"TransferTo: 源格子 {fromSlotIndex} 数量不足，需要 {quantity}，当前持有 {sourceSlot.Quantity}");
            return false;
        }

        // ── 检查目标物品栏是否有足够空间 ──
        int maxAddable = target.GetMaxAddableAmount(sourceSlot.Item);
        if (maxAddable < quantity)
        {
            Debug.LogWarning($"TransferTo: 目标物品栏空间不足，需要 {quantity}，目标最多可容纳 {maxAddable}，无法转移 '{sourceSlot.Item.ItemName}'");
            return false;
        }

        // ── 从源格子移除 ──
        var item = sourceSlot.Item;
        sourceSlot.Quantity -= quantity;
        if (sourceSlot.Quantity <= 0)
        {
            sourceSlot.Item = null;
            sourceSlot.Quantity = 0;
        }

        NotifySlotChanged(fromSlotIndex);

        // ── 添加到目标物品栏（此时已确保空间足够，不会部分丢失） ──
        target.AddItem(item, quantity);
        return true;
    }

    // ── Utilities ──

    /// <summary>
    /// 通知指定格子已变更，触发 OnSlotChanged 和 OnInventoryChanged。
    /// </summary>
    private void NotifySlotChanged(int slotIndex)
    {
        try { OnSlotChanged?.Invoke(slotIndex); }
        catch (Exception e) { Debug.LogWarning($"OnSlotChanged 事件处理异常: {e.Message}"); }

        try { OnInventoryChanged?.Invoke(); }
        catch (Exception e) { Debug.LogWarning($"OnInventoryChanged 事件处理异常: {e.Message}"); }
    }

    /// <summary>
    /// 通知物品栏已整体变更，触发 OnInventoryChanged。
    /// </summary>
    private void NotifyInventoryChanged()
    {
        try { OnInventoryChanged?.Invoke(); }
        catch (Exception e) { Debug.LogWarning($"OnInventoryChanged 事件处理异常: {e.Message}"); }
    }

    /// <summary>
    /// 检查索引是否在有效范围内。
    /// </summary>
    private bool IsValidIndex(int index)
    {
        return index >= 0 && index < _slots.Count;
    }

    /// <summary>
    /// 确保 _slots 列表始终具有 _maxSlots 个条目，不足则填充空格子。
    /// </summary>
    private void EnsureSlotsInitialized()
    {
        while (_slots.Count < _maxSlots)
        {
            _slots.Add(new InventorySlot());
        }
    }
}
