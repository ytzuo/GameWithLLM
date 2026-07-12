public abstract class UIToast : UIBase
{
    public override void Display() => UIManager.Instance.ShowToastElement(this);
    public override void Hide() => UIManager.Instance.HideToastElement(this);
    
    public abstract void SetMessage(string message);
}