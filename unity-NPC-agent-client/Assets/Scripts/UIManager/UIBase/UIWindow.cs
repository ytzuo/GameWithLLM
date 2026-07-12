public abstract class UIWindow : UIBase
{
    public override void Display() => UIManager.Instance.ShowWindow(this);
    public override void Hide() => UIManager.Instance.HideWindow(this);
}