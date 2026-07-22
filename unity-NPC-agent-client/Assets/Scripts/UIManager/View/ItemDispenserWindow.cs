using System;
using UnityEngine;
using UnityEngine.UIElements;

public class ItemDispenserWindow : BaseWindow
{
    // ── 事件 ────────────────────────────────────────────────

    public event Action Closed;

    // ── 外部数据 ────────────────────────────────────────────

    /// <summary>
    /// 物品静态数据表，在 Open 之前或之后由外部设置。
    /// </summary>
    private ItemDataList _itemDataList;
    public ItemDataList ItemDataList
    {
        get => _itemDataList;
        set
        {
            _itemDataList = value;
            // RefreshCatalogGrid 依赖 _catalogGrid 已绑定，若窗口尚未 Load 则安全跳过
            if (_catalogGrid != null)
                RefreshCatalogGrid();
        }
    }

    // ── UI 元素 ──────────────────────────────────────────────

    private VisualTreeAsset _slotTemplate;
    private VisualElement _containerGrid;
    private VisualElement _catalogGrid;
    private VisualElement _containerList;
    private Label _infoName;
    private Label _infoDesc;
    private Label _infoId;
    private Button _dispenseButton;

    // ── 数据 ────────────────────────────────────────────────

    private InventoryComponent _selectedTarget;
    private ItemData _selectedItem;
    private VisualElement _selectedListItem;

    // ── 订阅处理器 ──────────────────────────────────────────

    private Action _onRegistryChangedHandler;
    private Action _onTargetInventoryChangedHandler;
    private Action<int> _onTargetSlotChangedHandler;

    // ── Lifecycle ───────────────────────────────────────────

    protected override void OnBindElements()
    {
        _containerGrid = RootElement.Q<VisualElement>("dispenser-container-grid");
        _catalogGrid = RootElement.Q<VisualElement>("dispenser-catalog-grid");
        _containerList = RootElement.Q<VisualElement>("dispenser-list-container");
        _infoName = RootElement.Q<Label>("dispenser-info-name");
        _infoDesc = RootElement.Q<Label>("dispenser-info-desc");
        _infoId = RootElement.Q<Label>("dispenser-info-id");
        _dispenseButton = RootElement.Q<Button>("dispenser-dispense-btn");

        _slotTemplate = Resources.Load<VisualTreeAsset>("UI/Inventory/InventorySlot");

        if (_containerGrid == null)
            Debug.LogError("ItemDispenserWindow: dispenser-container-grid 未在 UXML 中找到。");
        if (_catalogGrid == null)
            Debug.LogError("ItemDispenserWindow: dispenser-catalog-grid 未在 UXML 中找到。");
        if (_containerList == null)
            Debug.LogError("ItemDispenserWindow: dispenser-list-container 未在 UXML 中找到。");
        if (_infoName == null)
            Debug.LogWarning("ItemDispenserWindow: dispenser-info-name 未在 UXML 中找到。");
        if (_infoDesc == null)
            Debug.LogWarning("ItemDispenserWindow: dispenser-info-desc 未在 UXML 中找到。");
        if (_infoId == null)
            Debug.LogWarning("ItemDispenserWindow: dispenser-info-id 未在 UXML 中找到。");
        if (_dispenseButton == null)
            Debug.LogError("ItemDispenserWindow: dispenser-dispense-btn 未在 UXML 中找到。");
        if (_slotTemplate == null)
            Debug.LogError("ItemDispenserWindow: 无法加载 InventorySlot 模板。");
    }

    protected override void OnOpen()
    {
        _onRegistryChangedHandler = () => RefreshContainerList();
        InventoryViewModel.Instance.OnContainerRegistryChanged += _onRegistryChangedHandler;

        RefreshContainerList();
        RefreshCatalogGrid();

        if (_dispenseButton != null)
            _dispenseButton.clicked += OnDispenseClicked;
        _dispenseButton?.SetEnabled(false);
    }

    protected override void OnClose()
    {
        InventoryViewModel.Instance.OnContainerRegistryChanged -= _onRegistryChangedHandler;
        _onRegistryChangedHandler = null;

        if (_dispenseButton != null)
            _dispenseButton.clicked -= OnDispenseClicked;

        UnsubscribeFromTargetInventory();

        try { Closed?.Invoke(); }
        catch (Exception e)
        {
            Debug.LogWarning($"ItemDispenserWindow: Error invoking Closed event: {e.Message}");
        }
    }

    public override void OnDestroy()
    {
        InventoryViewModel.Instance.OnContainerRegistryChanged -= _onRegistryChangedHandler;
        _onRegistryChangedHandler = null;

        if (_dispenseButton != null)
            _dispenseButton.clicked -= OnDispenseClicked;

        UnsubscribeFromTargetInventory();

        base.OnDestroy();
    }

    // ── Right Panel: Container List ─────────────────────────

    private void RefreshContainerList()
    {
        if (_containerList == null) return;

        _containerList.Clear();

        var containers = InventoryViewModel.Instance.GetAllContainers();
        var playerInv = InventoryViewModel.Instance.PlayerInventory;

        bool hasAny = false;
        foreach (var (component, name) in containers)
        {
            // 跳过玩家物品栏
            if (component == playerInv) continue;

            hasAny = true;

            var itemElement = new VisualElement();
            itemElement.AddToClassList("dispenser-container-item");

            var nameLabel = new Label(name);
            nameLabel.AddToClassList("dispenser-container-item-name");
            itemElement.Add(nameLabel);

            itemElement.RegisterCallback<ClickEvent>(_ =>
            {
                SelectTarget(component, itemElement);
            });

            _containerList.Add(itemElement);
        }

        if (!hasAny)
        {
            var emptyLabel = new Label("没有可用的容器");
            emptyLabel.AddToClassList("inv-list-empty");
            _containerList.Add(emptyLabel);
        }
    }

    // ── Target Selection ────────────────────────────────────

    private void SelectTarget(InventoryComponent target, VisualElement listItem)
    {
        if (target == null) return;

        // 取消订阅旧目标
        UnsubscribeFromTargetInventory();

        _selectedTarget = target;

        // 更新右侧列表高亮
        if (_selectedListItem != null)
            _selectedListItem.RemoveFromClassList("dispenser-container-item--selected");

        _selectedListItem = listItem;

        if (_selectedListItem != null)
            _selectedListItem.AddToClassList("dispenser-container-item--selected");

        // 订阅新目标
        SubscribeToTargetInventory();

        RefreshContainerGrid();
    }

    // ── Subscription ────────────────────────────────────────

    private void SubscribeToTargetInventory()
    {
        if (_selectedTarget == null) return;

        _onTargetInventoryChangedHandler = OnTargetInventoryChanged;
        _onTargetSlotChangedHandler = OnTargetSlotChanged;

        _selectedTarget.OnInventoryChanged += _onTargetInventoryChangedHandler;
        _selectedTarget.OnSlotChanged += _onTargetSlotChangedHandler;
    }

    private void UnsubscribeFromTargetInventory()
    {
        if (_selectedTarget == null) return;

        if (_onTargetInventoryChangedHandler != null)
            _selectedTarget.OnInventoryChanged -= _onTargetInventoryChangedHandler;
        if (_onTargetSlotChangedHandler != null)
            _selectedTarget.OnSlotChanged -= _onTargetSlotChangedHandler;

        _onTargetInventoryChangedHandler = null;
        _onTargetSlotChangedHandler = null;
    }

    private void OnTargetInventoryChanged()
    {
        RefreshContainerGrid();
    }

    private void OnTargetSlotChanged(int index)
    {
        RefreshSingleContainerSlot(index);
    }

    // ── Left Panel: Container Grid ──────────────────────────

    private void RefreshContainerGrid()
    {
        if (_containerGrid == null) return;

        _containerGrid.Clear();

        if (_selectedTarget == null) return;

        var slots = _selectedTarget.Slots;
        if (slots == null) return;

        for (int i = 0; i < slots.Count; i++)
        {
            CreateContainerSlotElement(i, slots[i]);
        }
    }

    private void RefreshSingleContainerSlot(int index)
    {
        if (_containerGrid == null || _selectedTarget == null) return;
        if (index >= _containerGrid.childCount) return;

        var slot = _selectedTarget.GetSlot(index);
        if (slot == null) return;

        VisualElement slotElement = _containerGrid.ElementAt(index);
        ApplySlotVisual(slotElement, slot);
    }

    private void CreateContainerSlotElement(int index, InventorySlot slot)
    {
        if (_containerGrid == null || _slotTemplate == null || slot == null) return;

        VisualElement slotElement = _slotTemplate.CloneTree();
        ApplySlotVisual(slotElement, slot);
        _containerGrid.Add(slotElement);
    }

    // ── Left Panel: Catalog Grid ────────────────────────────

    private void RefreshCatalogGrid()
    {
        if (_catalogGrid == null) return;

        _catalogGrid.Clear();

        if (ItemDataList == null || ItemDataList.items == null) return;

        foreach (var item in ItemDataList.items)
        {
            if (item == null) continue;

            VisualElement slotElement = _slotTemplate.CloneTree();

            var icon = slotElement.Q<VisualElement>("slot-icon");
            if (icon != null && item.Icon != null)
            {
                icon.style.backgroundImage = new StyleBackground(item.Icon);
            }

            var qty = slotElement.Q<Label>("slot-quantity");
            if (qty != null)
            {
                qty.AddToClassList("inv-slot-quantity--hidden");
            }

            // catalog 物品不显示 empty 样式
            slotElement.RemoveFromClassList("inv-slot--empty");

            slotElement.RegisterCallback<ClickEvent>(_ =>
            {
                SelectCatalogItem(item);
            });

            _catalogGrid.Add(slotElement);
        }
    }

    // ── Catalog Selection ───────────────────────────────────

    private void SelectCatalogItem(ItemData item)
    {
        if (item == null) return;

        _selectedItem = item;

        if (_infoName != null)
            _infoName.text = item.ItemName;
        if (_infoDesc != null)
            _infoDesc.text = item.Description ?? string.Empty;
        if (_infoId != null)
            _infoId.text = $"ID: {item.ItemId}  |  最大堆叠: {item.MaxStackSize}";

        _dispenseButton?.SetEnabled(true);
    }

    // ── Slot Visual ─────────────────────────────────────────

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

    // ── Bottom Bar: Dispense ────────────────────────────────

    private void OnDispenseClicked()
    {
        if (_selectedTarget == null || _selectedItem == null) return;

        if (_selectedTarget.AddItem(_selectedItem, 1))
        {
            Debug.Log($"物品发放：{_selectedItem.ItemName} → {InventoryViewModel.Instance.GetContainerName(_selectedTarget)}");
            RefreshContainerGrid();
        }
        else
        {
            Debug.LogWarning($"发放失败：容器已满，无法添加 {_selectedItem.ItemName}");
        }
    }
}
