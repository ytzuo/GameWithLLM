using System;
using UnityEngine;
using UnityEngine.UIElements;

public class InventoryInteractWindow : BaseWindow
{
    // ── 事件 ────────────────────────────────────────────────

    public event Action Closed;

    // ── UI ──────────────────────────────────────────────────

    private VisualTreeAsset _slotTemplate;
    private VisualElement _targetGrid;
    private VisualElement _playerGrid;
    private Label _targetLabel;
    private Label _playerLabel;

    // ── 数据 ────────────────────────────────────────────────

    private InventoryComponent _targetInventory;
    private InventoryComponent _playerInventory;

    // ── 订阅处理器 ──────────────────────────────────────────

    private Action _onTargetChangedHandler;
    private Action _onPlayerChangedHandler;
    private Action<int> _onTargetSlotHandler;
    private Action<int> _onPlayerSlotHandler;

    // ── Lifecycle ───────────────────────────────────────────

    protected override void OnBindElements()
    {
        _targetGrid = RootElement.Q<VisualElement>("inv-grid-2");
        _playerGrid = RootElement.Q<VisualElement>("inv-grid-1");
        _targetLabel = RootElement.Q<Label>("inv-section-label-2");
        _playerLabel = RootElement.Q<Label>("inv-section-label-1");

        _slotTemplate = Resources.Load<VisualTreeAsset>("UI/Inventory/InventorySlot");

        if (_targetGrid == null)
            Debug.LogError("InventoryInteractWindow: inv-grid-2 未在 UXML 中找到。");
        if (_playerGrid == null)
            Debug.LogError("InventoryInteractWindow: inv-grid-1 未在 UXML 中找到。");
        if (_targetLabel == null)
            Debug.LogWarning("InventoryInteractWindow: inv-section-label-2 未在 UXML 中找到。");
        if (_playerLabel == null)
            Debug.LogWarning("InventoryInteractWindow: inv-section-label-1 未在 UXML 中找到。");
        if (_slotTemplate == null)
            Debug.LogError("InventoryInteractWindow: 无法加载 InventorySlot 模板。");
    }

    protected override void OnOpen()
    {
        if (_playerInventory != null)
            SubscribeToPlayerInventory();
        if (_targetInventory != null)
            SubscribeToTargetInventory();

        if (_playerInventory != null)
            RefreshPlayerGrid();
        if (_targetInventory != null)
            RefreshTargetGrid();
    }

    protected override void OnClose()
    {
        UnsubscribeFromTargetInventory();
        UnsubscribeFromPlayerInventory();
        Closed?.Invoke();
    }

    public override void OnDestroy()
    {
        UnsubscribeFromTargetInventory();
        UnsubscribeFromPlayerInventory();
        base.OnDestroy();
    }

    // ── 公开方法 ────────────────────────────────────────────

    /// <summary>
    /// 设置交互双方物品栏。
    /// </summary>
    /// <param name="player">玩家物品栏。</param>
    /// <param name="target">目标容器物品栏。</param>
    /// <param name="targetName">目标容器显示名称。</param>
    public void SetInventories(InventoryComponent player, InventoryComponent target, string targetName)
    {
        bool targetChanged = _targetInventory != target;
        bool playerChanged = _playerInventory != player;

        // 先解绑旧引用，再一次性更新交互双方。格子点击回调会同时捕获双方引用，
        // 因此不能在另一方尚未赋值时提前刷新任意一侧。
        if (IsOpen)
        {
            if (targetChanged && _targetInventory != null)
                UnsubscribeFromTargetInventory();
            if (playerChanged && _playerInventory != null)
                UnsubscribeFromPlayerInventory();
        }

        _targetInventory = target;
        _playerInventory = player;

        if (IsOpen)
        {
            if (targetChanged && _targetInventory != null)
                SubscribeToTargetInventory();
            if (playerChanged && _playerInventory != null)
                SubscribeToPlayerInventory();

            // 两侧都在引用更新后重建，确保 Shift+Click 回调持有当前交互对象。
            RefreshTargetGrid();
            RefreshPlayerGrid();
        }

        // ── 更新标题 ──
        if (_targetLabel != null)
            _targetLabel.text = targetName ?? "目标容器";
        if (_playerLabel != null)
            _playerLabel.text = "玩家物品栏";

        Debug.Log($"InventoryInteractWindow: 设置交互——玩家 ↔ {targetName}");
    }

    // ── Subscription ────────────────────────────────────────

    private void SubscribeToTargetInventory()
    {
        if (_targetInventory == null) return;

        _onTargetChangedHandler = OnTargetInventoryChanged;
        _onTargetSlotHandler = OnTargetSlotChanged;

        _targetInventory.OnInventoryChanged += _onTargetChangedHandler;
        _targetInventory.OnSlotChanged += _onTargetSlotHandler;
    }

    private void UnsubscribeFromTargetInventory()
    {
        if (_targetInventory == null) return;

        if (_onTargetChangedHandler != null)
            _targetInventory.OnInventoryChanged -= _onTargetChangedHandler;
        if (_onTargetSlotHandler != null)
            _targetInventory.OnSlotChanged -= _onTargetSlotHandler;

        _onTargetChangedHandler = null;
        _onTargetSlotHandler = null;
    }

    private void SubscribeToPlayerInventory()
    {
        if (_playerInventory == null) return;

        _onPlayerChangedHandler = OnPlayerInventoryChanged;
        _onPlayerSlotHandler = OnPlayerSlotChanged;

        _playerInventory.OnInventoryChanged += _onPlayerChangedHandler;
        _playerInventory.OnSlotChanged += _onPlayerSlotHandler;
    }

    private void UnsubscribeFromPlayerInventory()
    {
        if (_playerInventory == null) return;

        if (_onPlayerChangedHandler != null)
            _playerInventory.OnInventoryChanged -= _onPlayerChangedHandler;
        if (_onPlayerSlotHandler != null)
            _playerInventory.OnSlotChanged -= _onPlayerSlotHandler;

        _onPlayerChangedHandler = null;
        _onPlayerSlotHandler = null;
    }

    private void OnTargetInventoryChanged()
    {
        RefreshTargetGrid();
    }

    private void OnTargetSlotChanged(int index)
    {
        RefreshSingleSlot(_targetGrid, index, _targetInventory.GetSlot(index), false);
    }

    private void OnPlayerInventoryChanged()
    {
        RefreshPlayerGrid();
    }

    private void OnPlayerSlotChanged(int index)
    {
        RefreshSingleSlot(_playerGrid, index, _playerInventory.GetSlot(index), true);
    }

    // ── Rendering ───────────────────────────────────────────

    private void RefreshTargetGrid()
    {
        if (_targetGrid == null || _targetInventory == null) return;
        _targetGrid.Clear();
        var slots = _targetInventory.Slots;
        if (slots == null) return;
        for (int i = 0; i < slots.Count; i++)
        {
            CreateSlotElement(i, slots[i], false);
        }
    }

    private void RefreshPlayerGrid()
    {
        if (_playerGrid == null || _playerInventory == null) return;
        _playerGrid.Clear();
        var slots = _playerInventory.Slots;
        if (slots == null) return;
        for (int i = 0; i < slots.Count; i++)
        {
            CreateSlotElement(i, slots[i], true);
        }
    }

    private void RefreshSingleSlot(VisualElement grid, int index, InventorySlot slot, bool isPlayerInventory)
    {
        if (grid == null || slot == null) return;
        if (index >= grid.childCount) return;

        VisualElement slotElement = grid.ElementAt(index);
        ApplySlotVisual(slotElement, slot);
    }

    private void CreateSlotElement(int index, InventorySlot slot, bool isPlayerInventory)
    {
        VisualElement grid = isPlayerInventory ? _playerGrid : _targetGrid;
        InventoryComponent owningInventory = isPlayerInventory ? _playerInventory : _targetInventory;
        InventoryComponent otherInventory = isPlayerInventory ? _targetInventory : _playerInventory;

        if (grid == null || _slotTemplate == null || slot == null) return;

        VisualElement slotElement = _slotTemplate.CloneTree();
        ApplySlotVisual(slotElement, slot);

        // ── Shift+Click 交互 ──
        slotElement.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.shiftKey)
            {
                OnSlotClicked(index, owningInventory, otherInventory);
            }
        });

        grid.Add(slotElement);
    }

    private void ApplySlotVisual(VisualElement slotElement, InventorySlot slot)
    {
        var iconElement = slotElement.Q<VisualElement>("slot-icon");
        var quantityLabel = slotElement.Q<Label>("slot-quantity");

        if (slot.IsEmpty)
        {
            slotElement.AddToClassList("inv-slot--empty");
            if (iconElement != null)
                iconElement.style.backgroundImage = StyleKeyword.None;
            if (quantityLabel != null)
            {
                quantityLabel.text = string.Empty;
                quantityLabel.AddToClassList("inv-slot-quantity--hidden");
            }
        }
        else
        {
            slotElement.RemoveFromClassList("inv-slot--empty");

            if (iconElement != null && slot.Item != null && slot.Item.Icon != null)
            {
                iconElement.style.backgroundImage = new StyleBackground(slot.Item.Icon);
            }

            if (quantityLabel != null)
            {
                if (slot.Quantity <= 1)
                {
                    quantityLabel.text = string.Empty;
                    quantityLabel.AddToClassList("inv-slot-quantity--hidden");
                }
                else
                {
                    quantityLabel.text = slot.Quantity.ToString();
                    quantityLabel.RemoveFromClassList("inv-slot-quantity--hidden");
                }
            }
        }
    }

    // ── Interaction ─────────────────────────────────────────

    /// <summary>
    /// Shift+Click 格子的回调。从 from 物品栏转移物品到 to 物品栏。
    /// </summary>
    /// <param name="slotIndex">被点击的格子索引。</param>
    /// <param name="from">源物品栏（被点击的那一侧）。</param>
    /// <param name="to">目标物品栏（另一侧）。</param>
    private void OnSlotClicked(int slotIndex, InventoryComponent from, InventoryComponent to)
    {
        if (from == null || to == null)
        {
            Debug.LogWarning("InventoryInteractWindow: 转移失败——物品栏引用为空。");
            return;
        }

        var slot = from.GetSlot(slotIndex);
        if (slot == null || slot.IsEmpty)
        {
            Debug.Log("InventoryInteractWindow: 无法转移——格子为空。");
            return;
        }

        int quantity = slot.Quantity;
        string itemName = slot.Item != null ? slot.Item.ItemName : "未知物品";

        bool success = from.TransferTo(to, slotIndex, quantity);
        if (success)
        {
            Debug.Log($"已将 {quantity} 个 {itemName} 从 {from.name} 转移到 {to.name}。");
        }
        else
        {
            Debug.LogWarning($"转移 {itemName} 失败：目标物品栏可能空间不足。");
        }
    }
}
