using UnityEngine.UIElements;


public class DefaultToast : UIToast
{
    private Label _messageLabel;

    protected override void OnBindElements(VisualElement root)
    {
        // 注意：请确保你的 Toast.uxml 中包含一个名为 "label_message" 的 Label 控件
        _messageLabel = root.Q<Label>("label_message");
    }

    public override void SetMessage(string message)
    {
        if (_messageLabel != null)
        {
            _messageLabel.text = message;
        }
    }

    public override void OnOpen()
    {
        // 可选：在这里重置透明度，或者触发 UI Toolkit 的 transition 动画
        RootElement.style.opacity = 1;
    }

    public override void OnClose()
    {
        RootElement.style.opacity = 0;
    }
}
