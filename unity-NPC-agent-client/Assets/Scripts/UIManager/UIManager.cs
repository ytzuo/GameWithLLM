using System;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
    public class UIManager : MonoBehaviour
    {
        public static UIManager Instance { get; private set; }

        [Header("UI 资源配置")]
        public List<PanelConfig> panelConfigs;
        public List<WindowConfig> windowConfigs;
        public List<HUDConfig> hudConfigs;

        [Header("Toast 提示配置")]
        public ToastConfig toastConfig;

        // 配置结构体
        [Serializable] public struct PanelConfig { public PanelID id; public VisualTreeAsset uxml; public string className; }
        [Serializable] public struct WindowConfig { public WindowID id; public VisualTreeAsset uxml; public string className; }
        [Serializable] public struct HUDConfig { public HUDID id; public VisualTreeAsset uxml; public string className; }
        [Serializable] public struct ToastConfig { public VisualTreeAsset uxml; public string className; }

        private UIDocument _uiDocument;
        private VisualElement _root;

        // 四个渲染层级容器
        private VisualElement _layerHUD;
        private VisualElement _layerPanel;
        private VisualElement _layerWindow;
        private VisualElement _layerToast;

        // 实例化缓存（每个 ID 对应唯一实例）
        private Dictionary<PanelID, UIPanel> _panelCache = new Dictionary<PanelID, UIPanel>();
        private Dictionary<WindowID, UIWindow> _windowCache = new Dictionary<WindowID, UIWindow>();
        private Dictionary<HUDID, UIHUD> _hudCache = new Dictionary<HUDID, UIHUD>();

        // Toast 对象池
        private Queue<UIToast> _toastPool = new Queue<UIToast>();

        // 运行时状态记录
        private UIPanel _currentPanel;
        private List<UIWindow> _activeWindows = new List<UIWindow>();

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            
            _uiDocument = GetComponent<UIDocument>();
            _root = _uiDocument.rootVisualElement;
            
            InitializeLayers();
        }

        private void InitializeLayers()
        {
            // 按顺序添加，越晚添加渲染在越上方
            _layerHUD = CreateLayer("Layer_HUD");
            _layerHUD.pickingMode = PickingMode.Ignore; // HUD 层不拦截点击

            _layerPanel = CreateLayer("Layer_Panel");
            _layerPanel.pickingMode = PickingMode.Ignore; // 让背景透明的地方可以穿透到 3D 场景

            _layerWindow = CreateLayer("Layer_Window");
            _layerWindow.pickingMode = PickingMode.Ignore;
            
            _layerToast = CreateLayer("Layer_Toast");
            _layerToast.pickingMode = PickingMode.Ignore; // 提示不阻挡操作
        }

        private VisualElement CreateLayer(string layerName)
        {
            var layer = new VisualElement { name = layerName };
            layer.style.flexGrow = 1;
            layer.style.position = Position.Absolute;
            layer.style.left = 0; layer.style.right = 0;
            layer.style.top = 0; layer.style.bottom = 0;
            _root.Add(layer);
            return layer;
        }

        // ==========================================
        // 公开 API
        // ==========================================

        public void OpenPanel(PanelID id)
        {
            var panel = GetOrCreateUI(id, panelConfigs, _panelCache);
            if (panel != null) panel.Display();
        }

        public void ClosePanel(PanelID id)
        {
            if (_panelCache.TryGetValue(id, out var panel)) panel.Hide();
        }

        public void OpenWindow(WindowID id)
        {
            var window = GetOrCreateUI(id, windowConfigs, _windowCache);
            if (window != null) window.Display();
        }

        public void CloseWindow(WindowID id)
        {
            if (_windowCache.TryGetValue(id, out var window)) window.Hide();
        }

        public void OpenHUD(HUDID id)
        {
            var hud = GetOrCreateUI(id, hudConfigs, _hudCache);
            if (hud != null) hud.Display();
        }

        public void CloseHUD(HUDID id)
        {
            if (_hudCache.TryGetValue(id, out var hud)) hud.Hide();
        }

        public async void ShowToast(string message, float duration = 2.0f)
        {
            if (toastConfig.uxml == null)
            {
                Debug.LogError("UIManager: 未配置 Toast UXML！");
                return;
            }

            // 从对象池获取
            UIToast toast = GetToastFromPool();
            
            // 设置内容并展示
            toast.SetMessage(message);
            toast.Display();

            try
            {
                // Unity 6 现代异步等待，包含生命周期安全取消
                await Awaitable.WaitForSecondsAsync(duration, destroyCancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Manager被销毁（如切场景）导致取消，安全退出
                return;
            }

            // 等待结束，隐藏回收
            if (toast.RootElement.parent != null)
            {
                toast.Hide();
            }
        }

        // ==========================================
        // 供基类多态调用的 Internal 方法 (逻辑控制)
        // ==========================================

        internal void ShowPanel(UIPanel panel)
        {
            // 隐藏当前界面
            if (_currentPanel != null && _currentPanel != panel)
            {
                _currentPanel.Hide();
            }
            _currentPanel = panel;
            _layerPanel.Add(panel.RootElement);
            panel.RootElement.BringToFront(); // 新界面置顶
            panel.OnOpen();
        }

        internal void HidePanel(UIPanel panel)
        {
            if (panel.RootElement.parent != null)
            {
                panel.OnClose();
                panel.RootElement.RemoveFromHierarchy();
                if (_currentPanel == panel) _currentPanel = null;
            }
        }

        internal void ShowWindow(UIWindow window)
        {
            _layerWindow.Add(window.RootElement);
            window.RootElement.BringToFront(); // 新弹窗置顶叠加
            if (!_activeWindows.Contains(window)) _activeWindows.Add(window);
            window.OnOpen();
        }

        internal void HideWindow(UIWindow window)
        {
            if (window.RootElement.parent != null)
            {
                window.OnClose();
                window.RootElement.RemoveFromHierarchy();
                _activeWindows.Remove(window);
            }
        }

        internal void ShowHUD(UIHUD hud)
        {
            if (hud.RootElement.parent == null)
            {
                _layerHUD.Add(hud.RootElement);
                hud.OnOpen();
            }
        }

        internal void HideHUD(UIHUD hud)
        {
            if (hud.RootElement.parent != null)
            {
                hud.OnClose();
                hud.RootElement.RemoveFromHierarchy();
            }
        }

        internal void ShowToastElement(UIToast toast)
        {
            _layerToast.Add(toast.RootElement);
            toast.RootElement.BringToFront(); // 后弹出的提示在最上方
            toast.OnOpen();
        }

        internal void HideToastElement(UIToast toast)
        {
            if (toast.RootElement.parent != null)
            {
                toast.OnClose();
                toast.RootElement.RemoveFromHierarchy();
                // 放回对象池复用
                _toastPool.Enqueue(toast);
            }
        }

        // ==========================================
        // 内部工厂：反射实例化
        // ==========================================

        private TUI GetOrCreateUI<TUI, TEnum, TConfig>(TEnum id, List<TConfig> configs, Dictionary<TEnum, TUI> cache)
            where TUI : UIBase
            where TEnum : Enum
        {
            if (cache.TryGetValue(id, out var existing)) return existing;

            foreach (var config in configs)
            {
                // 使用反射而不是 dynamic，避免依赖 Microsoft.CSharp.dll
                var cfgType = config.GetType();
                var idField = cfgType.GetField("id");
                if (idField == null) continue;
                var idValue = idField.GetValue(config);
                if (idValue != null && idValue.Equals(id))
                {
                    var classNameField = cfgType.GetField("className");
                    var uxmlField = cfgType.GetField("uxml");

                    string className = classNameField?.GetValue(config) as string;
                    VisualTreeAsset uxml = uxmlField?.GetValue(config) as VisualTreeAsset;

                    if (string.IsNullOrEmpty(className))
                    {
                        Debug.LogError($"UIManager: 配置中 className 为空（ID: {id}）。");
                        return null;
                    }

                    Type type = Type.GetType(className);
                    if (type == null)
                    {
                        Debug.LogError($"UIManager: 未找到类名 '{className}'，请检查配置或命名空间！");
                        return null;
                    }

                    TUI instance = (TUI)Activator.CreateInstance(type);
                    instance.Initialize(uxml);
                    cache[id] = instance;
                    return instance;
                }
            }

            Debug.LogError($"UIManager: 未在面板/窗口中找到 ID '{id}' 的配置。");
            return null;
        }

        private UIToast GetToastFromPool()
        {
            if (_toastPool.Count > 0)
            {
                return _toastPool.Dequeue();
            }

            Type type = Type.GetType(toastConfig.className) ?? typeof(DefaultToast);
            UIToast newToast = (UIToast)Activator.CreateInstance(type);
            newToast.Initialize(toastConfig.uxml);
            return newToast;
        }
    }