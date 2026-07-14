using UnityEngine.UIElements;
using System.Collections.Generic;
using UnityEngine;

public class ChatWindow : UIWindow
{
    // 序列化字段：在 Inspector 中直接拖拽分配消息模板资源
    [SerializeField]
    private VisualTreeAsset _playerMessageTemplate;
    
    [SerializeField]
    private VisualTreeAsset _opponentMessageTemplate;
    
    [SerializeField]
    private VisualTreeAsset _systemMessageTemplate;

    // UI 元素缓存
    private ScrollView _chatScroll;
    private List<VisualElement> _messageList = new List<VisualElement>();

    protected override void OnBindElements(VisualElement root)
    {
        // 绑定滚动视图
        _chatScroll = root.Q<ScrollView>("chat-scroll");
        if (_chatScroll == null)
        {
            Debug.LogError("ChatWindow 未找到 chat-scroll 滚动视图！");
        }

        // 验证消息模板是否已设置
        if (_playerMessageTemplate == null || _opponentMessageTemplate == null || _systemMessageTemplate == null)
        {
            Debug.LogWarning("ChatWindow: 消息模板未完全配置，请在 Inspector 中拖拽模板资源或调用 SetMessageTemplates()");
        }
    }

    /// <summary>
    /// 设置消息模板资源
    /// </summary>
    public void SetMessageTemplates(VisualTreeAsset playerTemplate, VisualTreeAsset opponentTemplate, VisualTreeAsset systemTemplate)
    {
        _playerMessageTemplate = playerTemplate;
        _opponentMessageTemplate = opponentTemplate;
        _systemMessageTemplate = systemTemplate;
    }

    /// <summary>
    /// 添加玩家消息
    /// </summary>
    public void AddPlayerMessage(string content)
    {
        if (_playerMessageTemplate == null)
        {
            Debug.LogError("ChatWindow: 玩家消息模板未设置！");
            return;
        }

        var messageElement = _playerMessageTemplate.Instantiate();
        var textLabel = messageElement.Q<Label>("message-text");
        if (textLabel != null)
        {
            textLabel.text = content;
        }

        _chatScroll.Add(messageElement);
        _messageList.Add(messageElement);
        
        // 自动滚动到最新消息
        ScrollToBottom();
    }

    /// <summary>
    /// 添加对方消息
    /// </summary>
    public void AddOpponentMessage(string content)
    {
        if (_opponentMessageTemplate == null)
        {
            Debug.LogError("ChatWindow: 对方消息模板未设置！");
            return;
        }

        var messageElement = _opponentMessageTemplate.Instantiate();
        var textLabel = messageElement.Q<Label>("message-text");
        if (textLabel != null)
        {
            textLabel.text = content;
        }

        _chatScroll.Add(messageElement);
        _messageList.Add(messageElement);
        
        // 自动滚动到最新消息
        ScrollToBottom();
    }

    /// <summary>
    /// 添加系统消息
    /// </summary>
    public void AddSystemMessage(string content)
    {
        if (_systemMessageTemplate == null)
        {
            Debug.LogError("ChatWindow: 系统消息模板未设置！");
            return;
        }

        var messageElement = _systemMessageTemplate.Instantiate();
        var textLabel = messageElement.Q<Label>("message-text");
        if (textLabel != null)
        {
            textLabel.text = content;
        }

        _chatScroll.Add(messageElement);
        _messageList.Add(messageElement);
        
        // 自动滚动到最新消息
        ScrollToBottom();
    }

    /// <summary>
    /// 清空所有消息
    /// </summary>
    public void ClearAllMessages()
    {
        _chatScroll.Clear();
        _messageList.Clear();
    }

    /// <summary>
    /// 自动滚动到底部（最新消息）
    /// </summary>
    private void ScrollToBottom()
    {
        // 使用 schedule 在下一帧执行滚动，确保 UI 布局已更新
        if (_chatScroll != null)
        {
            _chatScroll.schedule.Execute(() =>
            {
                var contentContainer = _chatScroll.contentContainer;
                if (contentContainer.childCount > 0)
                {
                    var lastChild = contentContainer[contentContainer.childCount - 1];
                    _chatScroll.ScrollTo(lastChild);
                }
            }).ExecuteLater(0);
        }
    }
}








