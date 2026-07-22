using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class InventoryListWindow : BaseWindow
{
    // ── 事件 ────────────────────────────────────────────────

    public event Action Closed;
    public event Action<InventoryComponent> OnContainerSelected;

    // ── UI ──────────────────────────────────────────────────

    private ScrollView _scrollView;
    private VisualElement _listContainer;

    // ── 订阅 ────────────────────────────────────────────────

    private Action _onRegistryChangedHandler;

    // ── Lifecycle ───────────────────────────────────────────

    protected override void OnBindElements()
    {
        _scrollView = RootElement.Q<ScrollView>("inv-list-scroll");
        _listContainer = RootElement.Q<VisualElement>("inv-list-container");

        if (_scrollView == null)
            Debug.LogError("InventoryListWindow: inv-list-scroll 未在 UXML 中找到。");
        if (_listContainer == null)
            Debug.LogError("InventoryListWindow: inv-list-container 未在 UXML 中找到。");
    }

    protected override void OnOpen()
    {
        _onRegistryChangedHandler = PopulateList;
        InventoryViewModel.Instance.OnContainerRegistryChanged += _onRegistryChangedHandler;
        PopulateList();
    }

    protected override void OnClose()
    {
        if (_onRegistryChangedHandler != null)
            InventoryViewModel.Instance.OnContainerRegistryChanged -= _onRegistryChangedHandler;
        _onRegistryChangedHandler = null;
        Closed?.Invoke();
    }

    public override void OnDestroy()
    {
        if (_onRegistryChangedHandler != null)
            InventoryViewModel.Instance.OnContainerRegistryChanged -= _onRegistryChangedHandler;
        _onRegistryChangedHandler = null;
        base.OnDestroy();
    }

    // ── Rendering ───────────────────────────────────────────

    private void PopulateList()
    {
        if (_listContainer == null) return;

        _listContainer.Clear();

        var allContainers = InventoryViewModel.Instance.GetAllContainers();
        var playerInventory = InventoryViewModel.Instance.PlayerInventory;

        // 排除玩家物品栏
        var filtered = new List<(InventoryComponent component, string name)>();
        for (int i = 0; i < allContainers.Count; i++)
        {
            var entry = allContainers[i];
            if (entry.component != playerInventory)
                filtered.Add(entry);
        }

        if (filtered.Count == 0)
        {
            var emptyLabel = new Label("没有可用的容器");
            emptyLabel.AddToClassList("inv-list-empty");
            _listContainer.Add(emptyLabel);
            Debug.Log("InventoryListWindow: 没有可用的容器（已排除玩家物品栏）。");
            return;
        }

        for (int i = 0; i < filtered.Count; i++)
        {
            var entry = filtered[i];
            InventoryComponent component = entry.component;
            string displayName = entry.name;

            var item = new VisualElement();
            item.AddToClassList("inv-list-item");

            // 图标
            var icon = new VisualElement();
            icon.AddToClassList("inv-list-item-icon");
            item.Add(icon);

            // 名称
            var nameLabel = new Label(displayName);
            nameLabel.AddToClassList("inv-list-item-name");
            item.Add(nameLabel);

            // 箭头
            var arrow = new Label(">");
            arrow.AddToClassList("inv-list-item-arrow");
            item.Add(arrow);

            // 点击事件
            InventoryComponent capturedComponent = component;
            item.RegisterCallback<ClickEvent>(evt =>
            {
                OnContainerClicked(capturedComponent);
            });

            _listContainer.Add(item);
        }
    }

    // ── Interaction ─────────────────────────────────────────

    private void OnContainerClicked(InventoryComponent component)
    {
        Debug.Log($"容器被选中：{InventoryViewModel.Instance.GetContainerName(component)}");
        try { OnContainerSelected?.Invoke(component); }
        catch (Exception e)
        {
            Debug.LogWarning($"InventoryListWindow: 触发 OnContainerSelected 时出错：{e.Message}");
        }
    }
}
