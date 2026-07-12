using UnityEngine.UIElements;
public abstract class UIBase
{
    public VisualElement RootElement { get; private set; }

    public virtual void Initialize(VisualTreeAsset uxml)
    {
        RootElement = uxml.Instantiate();
        RootElement.style.flexGrow = 1;
        RootElement.style.position = Position.Absolute;
        RootElement.style.left = 0; RootElement.style.right = 0;
        RootElement.style.top = 0; RootElement.style.bottom = 0;

        OnBindElements(RootElement);
    }

    protected virtual void OnBindElements(VisualElement root) { }
    public virtual void OnOpen() { }
    public virtual void OnClose() { }
    
    public abstract void Display();
    public abstract void Hide();
}