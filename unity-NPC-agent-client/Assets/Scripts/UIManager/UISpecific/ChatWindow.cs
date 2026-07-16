using UnityEngine.UIElements;
using System.Collections.Generic;
using UnityEngine;

public class ChatWindow : UIWindow
{
    private VisualTreeAsset _playerMessageTemplate;
    private VisualTreeAsset _opponentMessageTemplate;
    private VisualTreeAsset _systemMessageTemplate;

    // UI 元素缓存
    private ScrollView _chatScroll;
    private List<VisualElement> _messageList = new List<VisualElement>();
    // 输入框相关
    private TextField _inputField;

    protected override void OnBindElements(VisualElement root)
    {
        LoadTemplatesFromResources();
        _chatScroll = root.Q<ScrollView>("chat-scroll");
        _inputField = root.Q<TextField>("chat-input");

        // 为输入框绑定回车键事件
        if (_inputField != null)
        {
            _inputField.RegisterCallback<KeyDownEvent>(OnInputFieldKeyDown);
        }

        // 验证消息模板是否已设置
        if (_playerMessageTemplate == null || _opponentMessageTemplate == null || _systemMessageTemplate == null)
        {
            Debug.LogWarning("ChatWindow: 消息模板未完全配置，请在 Inspector 中拖拽模板资源或调用 SetMessageTemplates()");
        }
    }
    
    /// <summary>
    /// 从 Resources 文件夹按路径加载消息模板（同步）。
    /// 路径为相对于 Resources 文件夹的路径，不包含扩展名，例如 "UI/Chat/PlayerMessage"。
    /// 注意：确保对应的 VisualTreeAsset (.uxml) 已放到 Assets/Resources/... 下。
    /// </summary>
    public void LoadTemplatesFromResources(string playerPath, string opponentPath, string systemPath)
    {
        _playerMessageTemplate = Resources.Load<VisualTreeAsset>(playerPath);
        _opponentMessageTemplate = Resources.Load<VisualTreeAsset>(opponentPath);
        _systemMessageTemplate = Resources.Load<VisualTreeAsset>(systemPath);

        if (_playerMessageTemplate == null || _opponentMessageTemplate == null || _systemMessageTemplate == null)
        {
            Debug.LogWarning($"ChatWindow: 从 Resources 加载消息模板时有未找到的资源。路径: player={playerPath}, opponent={opponentPath}, system={systemPath}");
        }
    }

    /// <summary>
    /// 使用默认路径从 Resources 加载模板（方便快速使用/原型）。
    /// 默认位置：Assets/Resources/UI/Chat/PlayerMessage.uxml 等
    /// </summary>
    public void LoadTemplatesFromResources()
    {
        LoadTemplatesFromResources("UI/Chat/PlayerMessage", "UI/Chat/OpponentMessage", "UI/Chat/SystemMessage");
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
    /// 从输入框读取内容并发送（当按回车时调用）
    /// </summary>
    private void SendMessageFromInput()
    {
        if (_inputField == null) return;

        var text = _inputField.value?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        AddPlayerMessage(text);

        // 发送后清空输入框，保留焦点以便继续输入
        _inputField.value = string.Empty;
        _inputField.Focus();
    }

    /// <summary>
    /// 输入框键盘事件处理（监听回车键）
    /// </summary>
    private void OnInputFieldKeyDown(KeyDownEvent evt)
    {
        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
        {
            SendMessageFromInput();
            evt.StopPropagation();
        }
    }

    /// <summary>
    /// 自动滚动到底部（最新消息）
    /// </summary>
    private void ScrollToBottom()
    {
        // 使用 schedule 重试方式确保元素完成布局后再滚动到末尾。
        // 有时新加入的元素在同一帧尚未完成布局，直接 ScrollTo 无效，所以重试几次。
        if (_chatScroll == null) return;

        int attempts = 0;
        const int maxAttempts = 8;

        // 递归调度：如果布局尚未完成则延迟再次尝试，直到达到最大次数
        System.Action tryScroll = null;
        tryScroll = () =>
        {
            var contentContainer = _chatScroll.contentContainer;
            if (contentContainer.childCount == 0)
            {
                attempts++;
                if (attempts < maxAttempts)
                {
                    _chatScroll.schedule.Execute(tryScroll).ExecuteLater(10);
                }
                return;
            }

            var lastChild = contentContainer[contentContainer.childCount - 1];
            if (lastChild.layout.height <= 0f && attempts < maxAttempts)
            {
                attempts++;
                _chatScroll.schedule.Execute(tryScroll).ExecuteLater(10);
                return;
            }

            _chatScroll.ScrollTo(lastChild);
        };

        // 首次尝试放到下一次调度（短延迟）确保 UI 布局开始更新
        _chatScroll.schedule.Execute(tryScroll).ExecuteLater(1);
    }
}








