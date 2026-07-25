using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class UIManager : MonoBehaviour
{
    private const string GameplayHudResourcePath = "UI/Hud/GameplayHud";

    public static UIManager Instance { get; private set; }
    private UIDocument _uiDocument;
    private VisualElement _gameplayHud;

    [Serializable]
    public struct UIConfig
    {
        public string WindowName;
        public VisualTreeAsset uxmlAsset;
    }
    [SerializeField] private List<UIConfig> serializedUiConfigs;
    // Initialize so we never hit a NullReferenceException when adding entries
    private Dictionary<string,VisualTreeAsset> _uiConfigs = new Dictionary<string, VisualTreeAsset>();
    
    // 集中管理所有被实例化的窗口
    private List<BaseWindow> _managedWindows = new List<BaseWindow>();

    /// <summary>
    /// 暴露 UIDocument 的根节点，供外部复用已存在的窗口
    /// </summary>
    public VisualElement RootVisualElement => _uiDocument.rootVisualElement;

    private void Awake()
    {
        Instance = this;
        _uiDocument = GetComponent<UIDocument>();
        CreateGameplayHud();
    }
    private void Start()
    {
        // Guard against the serialized list being null (e.g., not set in inspector)
        if (serializedUiConfigs == null) return;

        foreach (var uiConfig in serializedUiConfigs)
        {
            // Skip invalid or empty names
            if (string.IsNullOrEmpty(uiConfig.WindowName)) continue;

            // Use indexer to allow overwriting duplicates instead of throwing
            _uiConfigs[uiConfig.WindowName] = uiConfig.uxmlAsset;
        }
    }

    /// <summary>
    /// 打开一个新窗口
    /// 泛型 T 约束为 BaseWindow 且必须拥有无参构造函数 (new())
    /// </summary>
    public T OpenNewWindow<T>() where T : BaseWindow, new()
    {
        // 1. 实例化纯 C# 类
        T window = new T();
        VisualTreeAsset uxmlAsset = _uiConfigs[typeof(T).Name];
        // 2. 加载 Resources 下的 UXML
        if (uxmlAsset == null) throw new Exception($"UI config {typeof(T).Name} doesn't exist");
        
        window.Load(uxmlAsset);

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

    private void CreateGameplayHud()
    {
        VisualTreeAsset hudAsset =
            Resources.Load<VisualTreeAsset>(GameplayHudResourcePath);
        if (hudAsset == null)
        {
            Debug.LogWarning(
                $"UIManager: 无法加载常驻 HUD：Resources/{GameplayHudResourcePath}.uxml");
            return;
        }

        _gameplayHud = hudAsset.CloneTree();
        _gameplayHud.name = "gameplay-hud";
        DisablePickingRecursively(_gameplayHud);
        _uiDocument.rootVisualElement.Add(_gameplayHud);
        _gameplayHud.BringToFront();
        UpdateGameplayHudVisibility();
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

    private static void DisablePickingRecursively(VisualElement element)
    {
        if (element == null)
            return;

        element.pickingMode = PickingMode.Ignore;
        foreach (VisualElement child in element.Children())
            DisablePickingRecursively(child);
    }
}
