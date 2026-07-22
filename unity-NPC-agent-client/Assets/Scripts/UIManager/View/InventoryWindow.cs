using System;
using UnityEngine;
using UnityEngine.UIElements;

public class InventoryWindow : BaseWindow
{
    // ── 事件 ────────────────────────────────────────────────

    public event Action Closed;

    // ── 库存 UI ────────────────────────────────────────────
    private VisualTreeAsset _slotTemplate;
    private VisualElement _grid;
    private Label _panelTitle;

    // ── 数据 ────────────────────────────────────────────────
    private InventoryComponent _inventory;
    private Action _onInventoryChangedHandler;
    private Action<int> _onSlotChangedHandler;

    /// <summary>
    /// 设置要显示的物品栏，必须在 Open 之前调用。
    /// </summary>
    public InventoryComponent Inventory
    {
        get => _inventory;
        set
        {
            if (_inventory == value) return;

            // 如果已经打开，先取消旧订阅
            if (IsOpen && _inventory != null)
                UnsubscribeFromInventory();

            _inventory = value;

            // 如果已经打开，重新订阅并刷新
            if (IsOpen && _inventory != null)
            {
                SubscribeToInventory();
                RenderAllSlots();
            }
        }
    }

    // ── Lifecycle ───────────────────────────────────────────

    protected override void OnBindElements()
    {
        _grid = RootElement.Q<VisualElement>("inv-grid-1");
        _panelTitle = RootElement.Q<Label>("inv-panel-title");

        _slotTemplate = Resources.Load<VisualTreeAsset>("UI/Inventory/InventorySlot");

        if (_grid == null)
            Debug.LogError("InventoryWindow: inv-grid-1 未在 UXML 中找到。");
        if (_panelTitle == null)
            Debug.LogWarning("InventoryWindow: inv-panel-title 未在 UXML 中找到。");
        if (_slotTemplate == null)
            Debug.LogError("InventoryWindow: 无法加载 InventorySlot 模板。");
    }

    protected override void OnOpen()
    {
        if (_inventory != null)
        {
            SubscribeToInventory();
            RenderAllSlots();
        }
    }

    protected override void OnClose()
    {
        if (_inventory != null)
            UnsubscribeFromInventory();
        Closed?.Invoke();
    }

    public override void OnDestroy()
    {
        if (_inventory != null)
            UnsubscribeFromInventory();
        base.OnDestroy();
    }

    // ── Subscription ────────────────────────────────────────

    private void SubscribeToInventory()
    {
        if (_inventory == null) return;

        _onInventoryChangedHandler = OnInventoryChanged;
        _onSlotChangedHandler = OnSlotChanged;

        _inventory.OnInventoryChanged += _onInventoryChangedHandler;
        _inventory.OnSlotChanged += _onSlotChangedHandler;
    }

    private void UnsubscribeFromInventory()
    {
        if (_inventory == null) return;

        if (_onInventoryChangedHandler != null)
            _inventory.OnInventoryChanged -= _onInventoryChangedHandler;
        if (_onSlotChangedHandler != null)
            _inventory.OnSlotChanged -= _onSlotChangedHandler;

        _onInventoryChangedHandler = null;
        _onSlotChangedHandler = null;
    }

    private void OnInventoryChanged()
    {
        RenderAllSlots();
    }

    private void OnSlotChanged(int index)
    {
        RenderSlot(index, _inventory.GetSlot(index));
    }

    // ── Rendering ───────────────────────────────────────────

    private void RenderAllSlots()
    {
        if (_grid == null || _inventory == null) return;

        _grid.Clear();

        var slots = _inventory.Slots;
        if (slots == null) return;

        for (int i = 0; i < slots.Count; i++)
        {
            RenderSlot(i, slots[i]);
        }
    }

    private void RenderSlot(int index, InventorySlot slot)
    {
        if (_grid == null || _slotTemplate == null || slot == null) return;

        // 如果索引超出当前子元素数量，说明这是首次渲染，使用 CloneTree 添加
        VisualElement slotElement;
        if (index < _grid.childCount)
        {
            slotElement = _grid.ElementAt(index);
        }
        else
        {
            slotElement = _slotTemplate.CloneTree();
            _grid.Add(slotElement);
        }

        var iconElement = slotElement.Q<VisualElement>("slot-icon");
        var quantityLabel = slotElement.Q<Label>("slot-quantity");

        if (slot.IsEmpty)
        {
            // 空格子
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

            // 设置图标
            if (iconElement != null && slot.Item != null && slot.Item.Icon != null)
            {
                iconElement.style.backgroundImage = new StyleBackground(slot.Item.Icon);
            }

            // 设置数量
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
}
