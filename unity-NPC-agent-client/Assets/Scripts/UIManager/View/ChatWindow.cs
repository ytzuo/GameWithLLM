using System;
using UnityEngine;
using UnityEngine.UIElements;

public class ChatWindow : BaseWindow
{
    public event Action Closed;
    private const int MaxMessageLength = 1000;

    private ScrollView _chatScrollView;
    private TextField _chatInput;
    private Button _sendButton;
    private Button _closeButton;
    private Label _placeholder;
    private Label _characterCount;
    private bool _isSubscribed;
    private Action<Role, string> _viewModelHandler;
    private bool _historySynced;

    private VisualTreeAsset _systemMessageTemplate;
    private VisualTreeAsset _playerMessageTemplate;
    private VisualTreeAsset _opponentMessageTemplate;

    protected override void OnBindElements()
    {
        _chatScrollView = RootElement.Q<ScrollView>("chat-scroll");
        _chatInput = RootElement.Q<TextField>("chat-input");
        _sendButton = RootElement.Q<Button>("send-button");
        _closeButton = RootElement.Q<Button>("close-button");
        _placeholder = RootElement.Q<Label>("chat-placeholder");
        _characterCount = RootElement.Q<Label>("character-count");

        _systemMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/SystemMessage");
        _playerMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/PlayerMessage");
        _opponentMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/OpponentMessage");

        if (_chatScrollView == null || _chatInput == null)
            Debug.LogError("ChatWindow: required chat controls are missing from ChatView.uxml.");

        if (_systemMessageTemplate == null || _playerMessageTemplate == null || _opponentMessageTemplate == null)
            Debug.LogWarning("ChatWindow: one or more chat message templates failed to load.");

        RootElement.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

        if (_chatInput != null)
        {
            _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown);
            _chatInput.RegisterValueChangedCallback(OnInputValueChanged);
        }

        if (_sendButton != null)
            _sendButton.clicked += SendCurrentMessage;
        if (_closeButton != null)
            _closeButton.clicked += Close;

        UpdateInputState();
    }

    protected override void OnOpen()
    {
        if (!_historySynced)
        {
            _chatScrollView?.Clear();
            ChatViewModel.Instance.PopulateExistingHistory(RenderMessage);
            _historySynced = true;
        }

        if (!_isSubscribed)
        {
            _viewModelHandler = RenderMessage;
            ChatViewModel.Instance.Subscribe(_viewModelHandler);
            _isSubscribed = true;
        }

        RootElement.schedule.Execute(() =>
        {
            ScrollToLatestMessage();
            _chatInput?.Focus();
        });
    }

    protected override void OnClose()
    {
        if (_isSubscribed)
        {
            if (_viewModelHandler != null)
                ChatViewModel.Instance.Unsubscribe(_viewModelHandler);
            _viewModelHandler = null;
            _isSubscribed = false;
        }

        _chatInput?.Blur();
        Closed?.Invoke();
    }

    public override void OnDestroy()
    {
        RootElement.UnregisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

        if (_chatInput != null)
        {
            _chatInput.UnregisterCallback<KeyDownEvent>(OnChatInputKeyDown);
            _chatInput.UnregisterValueChangedCallback(OnInputValueChanged);
        }

        if (_sendButton != null)
            _sendButton.clicked -= SendCurrentMessage;
        if (_closeButton != null)
            _closeButton.clicked -= Close;

        base.OnDestroy();
    }

    private void OnRootKeyDown(KeyDownEvent evt)
    {
        if (evt.keyCode != KeyCode.Escape)
            return;

        Close();
        evt.StopImmediatePropagation();
    }
    private void OnChatInputKeyDown(KeyDownEvent evt)
    {
        if (evt == null)
            return;

        bool isEnter = evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;
        if (isEnter && !evt.shiftKey)
        {
            SendCurrentMessage();
            evt.StopImmediatePropagation();
        }
    }

    private void OnInputValueChanged(ChangeEvent<string> evt)
    {
        UpdateInputState();
    }

    private void UpdateInputState()
    {
        string value = _chatInput?.value ?? string.Empty;
        bool hasText = !string.IsNullOrWhiteSpace(value);

        if (_placeholder != null)
            _placeholder.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.Flex : DisplayStyle.None;

        if (_sendButton != null)
            _sendButton.SetEnabled(hasText);

        if (_characterCount != null)
        {
            _characterCount.text = $"{value.Length} / {MaxMessageLength}";
            _characterCount.EnableInClassList("character-count-warning", value.Length >= MaxMessageLength * 9 / 10);
        }
    }

    private void SendCurrentMessage()
    {
        string text = _chatInput?.value?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        ChatViewModel.Instance.AddPlayerMessage(text);
        _chatInput.value = string.Empty;
        RootElement.schedule.Execute(() => _chatInput?.Focus());
    }

    private void AddMessageFromTemplate(VisualTreeAsset template, string messageText)
    {
        if (template == null || _chatScrollView == null)
        {
            Debug.LogWarning("ChatWindow: cannot render a message because its template or ScrollView is missing.");
            return;
        }

        TemplateContainer container = template.CloneTree();
        Label label = container.Q<Label>("message-text") ?? container.Q<Label>();
        if (label == null)
        {
            Debug.LogWarning("ChatWindow: message template does not contain a Label.");
            return;
        }

        label.text = messageText;
        _chatScrollView.Add(container);
        container.schedule.Execute(() => _chatScrollView.ScrollTo(container));
    }

    private void ScrollToLatestMessage()
    {
        if (_chatScrollView?.contentContainer == null || _chatScrollView.contentContainer.childCount == 0)
            return;

        _chatScrollView.ScrollTo(_chatScrollView.contentContainer[_chatScrollView.contentContainer.childCount - 1]);
    }

    public enum Role
    {
        Player,
        Opponent,
        System
    }

    private void RenderMessage(Role role, string message)
    {
        switch (role)
        {
            case Role.Player:
                AddMessageFromTemplate(_playerMessageTemplate, message);
                break;
            case Role.Opponent:
                AddMessageFromTemplate(_opponentMessageTemplate, message);
                break;
            case Role.System:
                AddMessageFromTemplate(_systemMessageTemplate, message);
                break;
        }
    }
}