using UnityEngine;
using UnityEngine.UIElements;

public abstract class BaseWindow
{
    public VisualElement RootElement { get; private set; }
    
    // 标记当前窗口是否处于打开状态
    public bool IsOpen { get; private set; }

    /// <summary>
    /// 加载 UXML 资源并初始化根节点
    /// </summary>
    public void Load(VisualTreeAsset uxml)
    {
        if (uxml == null)
        {
            Debug.LogError("[UIManager]无法找到对应的 UXML 资源");
            return;
        }

        RootElement = uxml.Instantiate();
        
        // 使得该界面铺满父级容器（全屏覆盖）
        RootElement.style.flexGrow = 1;
        RootElement.style.position = Position.Absolute;
        RootElement.style.left = 0; RootElement.style.right = 0;
        RootElement.style.top = 0; RootElement.style.bottom = 0;

        OnBindElements();
    }

    /// <summary>
    /// 供子类重写：绑定内部控件 (类似 root.Q<Button>())
    /// </summary>
    protected virtual void OnBindElements() { }

    /// <summary>
    /// 挂载到屏幕上
    /// </summary>
    public void Open(VisualElement parentNode)
    {
        if (RootElement.parent == null)
        {
            parentNode.Add(RootElement);
        }
        RootElement.BringToFront(); // 保证新打开的在最上层
        IsOpen = true;
        OnOpen();
    }

    /// <summary>
    /// 从屏幕上移除（关闭但保留在内存中）
    /// </summary>
    public void Close()
    {
        if (!IsOpen)
            return;

        RootElement?.RemoveFromHierarchy();
        IsOpen = false;
        OnClose();
    }

    /// <summary>
    /// 彻底销毁前调用，供子类解绑事件防止内存泄漏
    /// </summary>
    public virtual void OnDestroy() { }

    protected virtual void OnOpen() { }
    protected virtual void OnClose() { }
}