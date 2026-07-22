using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/*
 InventoryViewModel 的职责：
 - 管理所有挂载了 InventoryComponent 的容器登记
 - 记录玩家物品栏引用（由 PlayerMock 设置）
 - 对外通过事件通知 UI 容器注册/注销变化
*/

public class InventoryViewModel
{
    // ── 单例 ─────────────────────────────────────────────

    private static InventoryViewModel _instance;
    private static readonly object _instanceLock = new object();
    public static InventoryViewModel Instance
    {
        get
        {
            lock (_instanceLock)
            {
                if (_instance == null)
                    _instance = new InventoryViewModel();
                return _instance;
            }
        }
    }
    private InventoryViewModel() { }

    // ── 线程锁 ───────────────────────────────────────────

    private readonly object _lock = new object();

    // ── Registry ─────────────────────────────────────────

    /// <summary>
    /// 所有挂载了 InventoryComponent 的容器。
    /// Key: InventoryComponent, Value: GameObject display name
    /// </summary>
    private readonly Dictionary<InventoryComponent, string> _containerRegistry =
        new Dictionary<InventoryComponent, string>();

    // ── 事件 ─────────────────────────────────────────────

    /// <summary>
    /// 容器注册/注销时触发。
    /// </summary>
    public event Action OnContainerRegistryChanged;

    // ── 玩家物品栏 ───────────────────────────────────────

    /// <summary>
    /// 玩家物品栏引用，由 PlayerMock 设置。
    /// </summary>
    public InventoryComponent PlayerInventory { get; set; }

    // ── 注册 / 注销 ──────────────────────────────────────

    /// <summary>
    /// 注册一个物品栏容器。
    /// </summary>
    public void RegisterContainer(InventoryComponent component, string displayName)
    {
        if (component == null)
        {
            Debug.LogWarning("InventoryViewModel: 无法注册空物品栏组件。");
            return;
        }

        lock (_lock)
        {
            if (_containerRegistry.ContainsKey(component))
            {
                Debug.LogWarning($"InventoryViewModel: 物品栏已注册：{displayName} ({component.GetInstanceID()})");
                return;
            }

            _containerRegistry.Add(component, displayName);
        }

        Debug.Log($"物品栏已注册：{displayName} ({component.GetInstanceID()})");
        NotifyContainerRegistryChanged();
    }

    /// <summary>
    /// 注销一个物品栏容器。
    /// </summary>
    public void UnregisterContainer(InventoryComponent component)
    {
        if (component == null)
        {
            Debug.LogWarning("InventoryViewModel: 无法注销空物品栏组件。");
            return;
        }

        lock (_lock)
        {
            if (!_containerRegistry.ContainsKey(component))
            {
                Debug.LogWarning($"InventoryViewModel: 物品栏未注册，无法注销：({component.GetInstanceID()})");
                return;
            }

            _containerRegistry.Remove(component);
        }

        Debug.Log($"物品栏已注销：({component.GetInstanceID()})");
        NotifyContainerRegistryChanged();
    }

    // ── 查询 ─────────────────────────────────────────────

    /// <summary>
    /// 获取所有已注册容器（返回副本）。
    /// </summary>
    public IReadOnlyList<(InventoryComponent component, string name)> GetAllContainers()
    {
        lock (_lock)
        {
            return _containerRegistry
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();
        }
    }

    /// <summary>
    /// 获取指定组件的显示名称。
    /// </summary>
    public string GetContainerName(InventoryComponent component)
    {
        if (component == null) return string.Empty;

        lock (_lock)
        {
            _containerRegistry.TryGetValue(component, out var name);
            return name ?? string.Empty;
        }
    }

    // ── 内部通知 ─────────────────────────────────────────

    private void NotifyContainerRegistryChanged()
    {
        try { OnContainerRegistryChanged?.Invoke(); }
        catch (Exception e)
        {
            Debug.LogWarning($"InventoryViewModel: Error notifying OnContainerRegistryChanged: {e.Message}");
        }
    }
}
