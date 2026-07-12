using UnityEngine.UIElements;


[UxmlElement]
public partial class PlayerMessageElement : VisualElement
{
    public PlayerMessageElement() {}
    // 可以在构造函数中直接传参
    public PlayerMessageElement(string content, VisualTreeAsset template)
    {
        // 1. 克隆树结构到自身
        template.CloneTree(this);

        // 2. 内部自行初始化数据
        var textLabel = this.Q<Label>("message-text");
        if (textLabel != null)
        {
            textLabel.text = content;
        }
    }
}

[UxmlElement]
public partial class OthersMessageElement : VisualElement
{
    public OthersMessageElement() {}
    // 可以在构造函数中直接传参
    public OthersMessageElement(string content, VisualTreeAsset template)
    {
        // 1. 克隆树结构到自身
        template.CloneTree(this);

        // 2. 内部自行初始化数据
        var textLabel = this.Q<Label>("message-text");
        if (textLabel != null)
        {
            textLabel.text = content;
        }
    }
}

[UxmlElement]
public partial class SystemMessageElement : VisualElement
{
    public SystemMessageElement() {}
    // 可以在构造函数中直接传参
    public SystemMessageElement(string content, VisualTreeAsset template)
    {
        // 1. 克隆树结构到自身
        template.CloneTree(this);

        // 2. 内部自行初始化数据
        var textLabel = this.Q<Label>("message-text");
        if (textLabel != null)
        {
            textLabel.text = content;
        }
    }
}