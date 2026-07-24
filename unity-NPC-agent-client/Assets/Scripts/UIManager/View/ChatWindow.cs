using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

public class ChatWindow : BaseWindow
{
    public event Action Closed;
    private const int MaxMessageLength = 1000;

    // ── 对话UI ──────────────────────────────────────────────
    private ScrollView _chatScrollView;
    private TextField _chatInput;
    private Button _sendButton;
    private Button _closeButton;
    private Label _placeholder;
    private Label _characterCount;
    private Label _chatTitle;

    // ── NPC 列表UI ──────────────────────────────────────────
    private VisualElement _npcListContainer;
    private string _activeNpcId;

    // ── 模板 ────────────────────────────────────────────────
    private VisualTreeAsset _systemMessageTemplate;
    private VisualTreeAsset _playerMessageTemplate;
    private VisualTreeAsset _opponentMessageTemplate;

    // ── ViewModel 订阅 ──────────────────────────────────────
    private bool _isSubscribed;
    private Action<Role, string> _viewModelMessageHandler;
    private Action<Role, string> _viewModelMessageUpdatedHandler;
    private Action<List<string>> _viewModelNpcListHandler;
    private Action<string> _viewModelActiveNpcHandler;
    private Label _latestOpponentMessageLabel;

    protected override void OnBindElements()
    {
        _chatScrollView = RootElement.Q<ScrollView>("chat-scroll");
        _chatInput = RootElement.Q<TextField>("chat-input");
        _sendButton = RootElement.Q<Button>("send-button");
        _closeButton = RootElement.Q<Button>("close-button");
        _placeholder = RootElement.Q<Label>("chat-placeholder");
        _characterCount = RootElement.Q<Label>("character-count");
        _chatTitle = RootElement.Q<Label>("chat-title");
        _npcListContainer = RootElement.Q<VisualElement>("npc-list-container");

        _systemMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/SystemMessage");
        _playerMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/PlayerMessage");
        _opponentMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/OpponentMessage");

        if (_chatScrollView == null || _chatInput == null)
            Debug.LogError("ChatWindow: required chat controls are missing from ChatView.uxml.");
        if (_npcListContainer == null)
            Debug.LogWarning("ChatWindow: npc-list-container is missing from ChatView.uxml.");

        if (_systemMessageTemplate == null || _playerMessageTemplate == null || _opponentMessageTemplate == null)
            Debug.LogWarning("ChatWindow: one or more chat message templates failed to load.");

        RootElement.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

        if (_chatInput != null)
        {
            _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown, TrickleDown.TrickleDown);
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
        if (!_isSubscribed)
        {
            _viewModelMessageHandler = RenderMessage;
            _viewModelMessageUpdatedHandler = UpdateRenderedMessage;
            _viewModelNpcListHandler = OnNpcListChanged;
            _viewModelActiveNpcHandler = OnActiveNpcChanged;

            ChatViewModel.Instance.Subscribe(_viewModelMessageHandler);
            ChatViewModel.Instance.SubscribeToUpdates(_viewModelMessageUpdatedHandler);
            ChatViewModel.Instance.OnNpcListChanged += _viewModelNpcListHandler;
            ChatViewModel.Instance.OnActiveNpcChanged += _viewModelActiveNpcHandler;
            _isSubscribed = true;
        }

        // 初始加载 NPC 列表
        RebuildNpcList(ChatViewModel.Instance.NpcIds);

        // 同步当前活跃 NPC 的历史
        string activeNpc = ChatViewModel.Instance.ActiveNpcId;
        if (!string.IsNullOrWhiteSpace(activeNpc))
        {
            _activeNpcId = activeNpc;
            HighlightActiveNpc(activeNpc);
            UpdateChatTitle(activeNpc);
            ReloadMessagesForActiveNpc();
        }

        RootElement.schedule.Execute(() =>
        {
            _chatInput?.Focus();
        });
    }

    protected override void OnClose()
    {
        if (_isSubscribed)
        {
            if (_viewModelMessageHandler != null)
                ChatViewModel.Instance.Unsubscribe(_viewModelMessageHandler);
            if (_viewModelMessageUpdatedHandler != null)
                ChatViewModel.Instance.UnsubscribeFromUpdates(_viewModelMessageUpdatedHandler);
            if (_viewModelNpcListHandler != null)
                ChatViewModel.Instance.OnNpcListChanged -= _viewModelNpcListHandler;
            if (_viewModelActiveNpcHandler != null)
                ChatViewModel.Instance.OnActiveNpcChanged -= _viewModelActiveNpcHandler;

            _viewModelMessageHandler = null;
            _viewModelMessageUpdatedHandler = null;
            _viewModelNpcListHandler = null;
            _viewModelActiveNpcHandler = null;
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
            _chatInput.UnregisterCallback<KeyDownEvent>(OnChatInputKeyDown, TrickleDown.TrickleDown);
            _chatInput.UnregisterValueChangedCallback(OnInputValueChanged);
        }

        if (_sendButton != null)
            _sendButton.clicked -= SendCurrentMessage;
        if (_closeButton != null)
            _closeButton.clicked -= Close;

        base.OnDestroy();
    }

    // ── NPC 列表 ────────────────────────────────────────────

    private void OnNpcListChanged(List<string> npcIds)
    {
        if (_npcListContainer == null) return;
        RebuildNpcList(npcIds);
    }

    private void OnActiveNpcChanged(string npcId)
    {
        if (string.IsNullOrWhiteSpace(npcId)) return;
        _activeNpcId = npcId;
        HighlightActiveNpc(npcId);
        UpdateChatTitle(npcId);
        ReloadMessagesForActiveNpc();
    }

    private void RebuildNpcList(IReadOnlyList<string> npcIds)
    {
        if (_npcListContainer == null) return;
        _npcListContainer.Clear();

        if (npcIds == null || npcIds.Count == 0)
            return;

        foreach (string npcId in npcIds)
        {
            if (string.IsNullOrWhiteSpace(npcId)) continue;

            var item = new Button { text = npcId };
            item.AddToClassList("npc-list-item");
            item.userData = npcId;

            if (npcId == _activeNpcId)
                item.AddToClassList("npc-list-item--active");

            item.clicked += () => OnNpcItemClicked(npcId);
            _npcListContainer.Add(item);
        }
    }

    private void OnNpcItemClicked(string npcId)
    {
        ChatViewModel.Instance.SelectNpc(npcId);
        _chatInput?.Focus();
    }

    private void HighlightActiveNpc(string activeNpcId)
    {
        if (_npcListContainer == null) return;
        foreach (var child in _npcListContainer.Children())
        {
            if (child is Button btn)
            {
                string id = btn.userData as string;
                if (id == activeNpcId)
                    btn.AddToClassList("npc-list-item--active");
                else
                    btn.RemoveFromClassList("npc-list-item--active");
            }
        }
    }

    private void UpdateChatTitle(string npcId)
    {
        if (_chatTitle != null)
            _chatTitle.text = string.IsNullOrWhiteSpace(npcId) ? "NPC 对话" : $"NPC 对话 — {npcId}";
    }

    private void ReloadMessagesForActiveNpc()
    {
        _chatScrollView?.Clear();
        _latestOpponentMessageLabel = null;
        ChatViewModel.Instance.PopulateExistingHistory(RenderMessage);
        RootElement.schedule.Execute(() => ScrollToLatestMessage());
    }

    // ── 输入处理 ────────────────────────────────────────────

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

        bool isEnter = evt.keyCode == KeyCode.Return ||
                       evt.keyCode == KeyCode.KeypadEnter ||
                       evt.character == '\n' ||
                       evt.character == '\r';
        if (!isEnter)
            return;

        if (evt.shiftKey)
            InsertLineBreak();
        else
            SendCurrentMessage();

        // TextField 会自行处理 Enter；必须在捕获阶段阻止默认行为，
        // 否则普通 Enter 会先换行，Shift+Enter 的行为也会因平台而异。
        evt.StopImmediatePropagation();
    }

    private void InsertLineBreak()
    {
        if (_chatInput == null)
            return;

        string value = _chatInput.value ?? string.Empty;
        int cursorIndex = Mathf.Clamp(_chatInput.cursorIndex, 0, value.Length);
        int selectIndex = Mathf.Clamp(_chatInput.selectIndex, 0, value.Length);
        int selectionStart = Mathf.Min(cursorIndex, selectIndex);
        int selectionEnd = Mathf.Max(cursorIndex, selectIndex);

        if (value.Length - (selectionEnd - selectionStart) >= MaxMessageLength)
            return;

        _chatInput.value = value.Substring(0, selectionStart) + "\n" + value.Substring(selectionEnd);
        int nextCursorIndex = selectionStart + 1;
        _chatInput.SelectRange(nextCursorIndex, nextCursorIndex);
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

        if (string.IsNullOrEmpty(_activeNpcId))
        {
            Debug.LogWarning("ChatWindow: 没有活跃 NPC，无法发送消息。");
            return;
        }

        ChatViewModel.Instance.AddPlayerMessage(text);
        _chatInput.value = string.Empty;
        RootElement.schedule.Execute(() => _chatInput?.Focus());
    }

    // ── 消息渲染 ────────────────────────────────────────────

    private Label AddMessageFromTemplate(VisualTreeAsset template, string messageText)
    {
        if (template == null || _chatScrollView == null)
        {
            Debug.LogWarning("ChatWindow: cannot render a message because its template or ScrollView is missing.");
            return null;
        }

        TemplateContainer container = template.CloneTree();
        Label label = container.Q<Label>("message-text") ?? container.Q<Label>();
        if (label == null)
        {
            Debug.LogWarning("ChatWindow: message template does not contain a Label.");
            return null;
        }

        label.text = messageText;
        _chatScrollView.Add(container);
        container.schedule.Execute(() => _chatScrollView.ScrollTo(container));
        return label;
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
                _latestOpponentMessageLabel = AddMessageFromTemplate(_opponentMessageTemplate, message);
                break;
            case Role.System:
                AddMessageFromTemplate(_systemMessageTemplate, message);
                break;
        }
    }

    private void UpdateRenderedMessage(Role role, string message)
    {
        if (role != Role.Opponent || _latestOpponentMessageLabel == null)
            return;

        _latestOpponentMessageLabel.text = message;
        RootElement.schedule.Execute(() => ScrollToLatestMessage());
    }
}
