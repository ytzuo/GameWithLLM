public abstract class UIPanel : UIBase
{
    // 直接使用单例
    public override void Display() => UIManager.Instance.ShowPanel(this);
    public override void Hide() => UIManager.Instance.HidePanel(this);
}