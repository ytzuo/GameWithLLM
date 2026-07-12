public abstract class UIHUD : UIBase
{
    public override void Display() => UIManager.Instance.ShowHUD(this);
    public override void Hide() => UIManager.Instance.HideHUD(this);
}