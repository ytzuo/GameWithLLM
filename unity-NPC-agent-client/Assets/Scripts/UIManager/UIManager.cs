using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class UIManager : MonoBehaviour
{
    public static UIManager Instance { get; private set; }
    private UIDocument _uiDocument;
    private VisualElement _gameplayHud;
    private VisualTreeAsset _gameplayHudAsset;
    private VisualElement _observedDocumentRoot;
    private int _stableDocumentRootFrames;
    private bool _hasLoggedHudAttached;
    private UiContentCatalog _contentCatalog;
    private bool _contentReady;

    private static readonly IReadOnlyDictionary<Type, string> WindowAddresses =
        new Dictionary<Type, string>
        {
            [typeof(ChatWindow)] = UiContentIds.ChatWindow,
            [typeof(InventoryListWindow)] = UiContentIds.InventoryListWindow,
            [typeof(InventoryInteractWindow)] = UiContentIds.InventoryInteractWindow,
            [typeof(InventoryWindow)] = UiContentIds.InventoryWindow,
            [typeof(ItemDispenserWindow)] = UiContentIds.ItemDispenserWindow,
            [typeof(SaveGameWindow)] = UiContentIds.SaveGameWindow
        };
    
    // 集中管理所有被实例化的窗口
    private List<BaseWindow> _managedWindows = new List<BaseWindow>();

    /// <summary>
    /// 暴露 UIDocument 的根节点，供外部复用已存在的窗口
    /// </summary>
    public VisualElement RootVisualElement => _uiDocument.rootVisualElement;
    public bool IsGameplayHudAttachedAndVisible =>
        _gameplayHud != null &&
        ReferenceEquals(_gameplayHud.parent, _uiDocument?.rootVisualElement) &&
        _gameplayHud.resolvedStyle.display != DisplayStyle.None;

    private void Awake()
    {
        Instance = this;
        _uiDocument = GetComponent<UIDocument>();
    }

    public void InitializeContent(UiContentCatalog catalog)
    {
        if (catalog == null || !catalog.IsReady)
            throw new InvalidOperationException("UI content catalog is not ready.");
        if (_contentCatalog != null && !ReferenceEquals(_contentCatalog, catalog))
            throw new InvalidOperationException("UI content cannot be replaced while the scene is active.");

        _contentCatalog = catalog;
        _uiDocument.panelSettings = catalog.PanelSettings;
        // 设置 PanelSettings 后，UIDocument 会在后续帧重建 rootVisualElement。
        // HUD 若立刻挂载到旧 root，会只显示一帧便随旧 root 脱离。
        _gameplayHudAsset = catalog.GetVisualTree(UiContentIds.GameplayHud);
        _gameplayHud?.RemoveFromHierarchy();
        _gameplayHud = null;
        _observedDocumentRoot = null;
        _stableDocumentRootFrames = 0;
        ApplyContentVisibility();
    }

    private void LateUpdate()
    {
        if (_gameplayHudAsset == null || _uiDocument == null)
            return;

        VisualElement currentRoot = _uiDocument.rootVisualElement;
        if (currentRoot == null)
            return;

        // Panel 重建时，新 root 也必须继续服从内容启动门控。
        currentRoot.style.display = _contentReady ? DisplayStyle.Flex : DisplayStyle.None;

        if (!ReferenceEquals(_observedDocumentRoot, currentRoot))
        {
            _observedDocumentRoot = currentRoot;
            _stableDocumentRootFrames = 0;
            return;
        }

        if (_stableDocumentRootFrames++ == 0)
            return;

        if (_gameplayHud == null || !ReferenceEquals(_gameplayHud.parent, currentRoot))
            AttachGameplayHud(currentRoot);
    }

    /// <summary>
    /// 打开一个新窗口
    /// 泛型 T 约束为 BaseWindow 且必须拥有无参构造函数 (new())
    /// </summary>
    public T OpenNewWindow<T>() where T : BaseWindow, new()
    {
        if (!_contentReady)
            throw new InvalidOperationException("UI content is not ready.");

        // 1. 实例化纯 C# 类
        T window = new T();
        if (_contentCatalog == null || !_contentCatalog.IsReady)
            throw new InvalidOperationException("UI content catalog is not ready.");
        if (!WindowAddresses.TryGetValue(typeof(T), out string address))
            throw new KeyNotFoundException($"UI address for {typeof(T).Name} is not registered.");
        VisualTreeAsset uxmlAsset = _contentCatalog.GetVisualTree(address);
        
        window.Load(uxmlAsset, _contentCatalog);

        // 3. 先纳入管理并监听状态，再挂载到 UIDocument。
        // 这样窗口打开事件触发时，HUD 可见性统计已经包含当前窗口。
        window.OpenStateChanged += OnWindowOpenStateChanged;
        _managedWindows.Add(window);
        window.Open(_uiDocument.rootVisualElement);

        return window;
    }

    /// <summary>
    /// 释放并彻底销毁指定窗口
    /// </summary>
    public void RemoveWindow(BaseWindow window)
    {
        if (window == null) return;

        // 如果还在显示，先从屏幕移除
        if (window.IsOpen)
        {
            window.Close();
        }
        
        // 触发子类的销毁逻辑（解绑事件等）
        window.OnDestroy();

        // 从管理列表中移除，等待 C# 的 GC 回收
        window.OpenStateChanged -= OnWindowOpenStateChanged;
        _managedWindows.Remove(window);
        UpdateGameplayHudVisibility();
    }

    /// <summary>
    /// 一键释放所有已关闭的窗口
    /// </summary>
    public void RemoveClosedWindows()
    {
        // 倒序遍历，安全地在循环中移除元素
        for (int i = _managedWindows.Count - 1; i >= 0; i--)
        {
            var window = _managedWindows[i];
            
            // 如果窗口状态是已关闭的
            if (!window.IsOpen)
            {
                window.OnDestroy();
                window.OpenStateChanged -= OnWindowOpenStateChanged;
                _managedWindows.RemoveAt(i);
            }
        }
        UpdateGameplayHudVisibility();
    }

    /// <summary>
    /// 重新打开一个已关闭的窗口（复用已有的 BaseWindow 实例）
    /// </summary>
    public void ReopenWindow(BaseWindow window)
    {
        if (window == null) return;
        if (window.IsOpen) return;
        window.Open(_uiDocument.rootVisualElement);
    }

    private void AttachGameplayHud(VisualElement documentRoot)
    {
        _gameplayHud?.RemoveFromHierarchy();
        if (_gameplayHud == null)
        {
            _gameplayHud = _gameplayHudAsset.CloneTree();
            _gameplayHud.name = "gameplay-hud";
            DisablePickingRecursively(_gameplayHud);
        }
        documentRoot.Add(_gameplayHud);
        _gameplayHud.BringToFront();
        UpdateGameplayHudVisibility();
        if (!_hasLoggedHudAttached)
        {
            _hasLoggedHudAttached = true;
            Debug.Log("[Content] Gameplay HUD attached to the stable UIDocument root.");
        }
    }

    private void OnWindowOpenStateChanged(BaseWindow window, bool isOpen)
    {
        UpdateGameplayHudVisibility();
    }

    private void UpdateGameplayHudVisibility()
    {
        if (_gameplayHud == null)
            return;

        bool hasOpenWindow = _managedWindows.Exists(
            window => window != null && window.IsOpen);
        _gameplayHud.style.display =
            hasOpenWindow ? DisplayStyle.None : DisplayStyle.Flex;
    }

    public void SetContentReady(bool ready)
    {
        _contentReady = ready;
        ApplyContentVisibility();
    }

    private void ApplyContentVisibility()
    {
        if (_uiDocument?.rootVisualElement == null)
            return;
        _uiDocument.rootVisualElement.style.display =
            _contentReady ? DisplayStyle.Flex : DisplayStyle.None;
    }

    private static void DisablePickingRecursively(VisualElement element)
    {
        if (element == null)
            return;

        element.pickingMode = PickingMode.Ignore;
        foreach (VisualElement child in element.Children())
            DisablePickingRecursively(child);
    }
}
