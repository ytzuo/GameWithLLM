using System;
using UnityEngine;
using UnityEngine.UIElements;


public class ChatWindow : BaseWindow
{
    // UI element references
    private ScrollView _chatScrollView;
    private TextField _chatInput;
    // whether we've subscribed to ChatViewModel events
    private bool _isSubscribed;
    // Flag used to detect that an incoming AddMessageToUI call originates from ChatViewModel event
    private bool _isHandlingViewModelEvent;
    // Store the delegate we subscribed so we can unsubscribe the exact instance
    private Action<Role, string> _viewModelHandler;
    // Guard to ensure history sync only happens once per window lifetime (not on reopen)
    private bool _historySynced;

    // Templates loaded from Resources/UI/Chat
    private VisualTreeAsset _systemMessageTemplate;
    private VisualTreeAsset _playerMessageTemplate;
    private VisualTreeAsset _opponentMessageTemplate;

    protected override void OnBindElements()
    {
        // Query main UI elements from the bound root UXML
        _chatScrollView = RootElement.Q<ScrollView>("chat-scroll");
        _chatInput = RootElement.Q<TextField>("chat-input");

        // Load UXML templates from Resources/UI/Chat (no extension)
        _systemMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/SystemMessage");
        _playerMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/PlayerMessage");
        _opponentMessageTemplate = Resources.Load<VisualTreeAsset>("UI/Chat/OpponentMessage");

        if (_chatScrollView == null)
            Debug.LogWarning("ChatWindow: could not find ScrollView 'chat-scroll' in the UXML root.");
        if (_chatInput == null)
            Debug.LogWarning("ChatWindow: could not find TextField 'chat-input' in the UXML root.");

        if (_systemMessageTemplate == null || _playerMessageTemplate == null || _opponentMessageTemplate == null)
            Debug.LogWarning("ChatWindow: one or more chat message templates failed to load from Resources/UI/Chat.");

        // Register Enter key handler for sending messages
        if (_chatInput != null)
        {
            _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown);
        }
    }

    // Open: sync state and register events
    protected override void OnOpen()
    {
        // Only sync history on first open; UI elements are preserved on reopen
        if (!_historySynced)
        {
            // 1. Clear and load existing history
            if (_chatScrollView != null)
                _chatScrollView.Clear();

            // Sync existing history and start listening for new messages
            ChatViewModel.Instance.PopulateExistingHistory(RenderMessage);
            _historySynced = true;
        }

        if (!_isSubscribed)
        {
            // Subscribe with a wrapper to mark that the call originates from ViewModel and avoid routing loops
            _viewModelHandler = (role, msg) =>
            {
                _isHandlingViewModelEvent = true;
                try
                {
                    RenderMessage(role, msg);
                }
                finally
                {
                    _isHandlingViewModelEvent = false;
                }
            };

            ChatViewModel.Instance.Subscribe(_viewModelHandler);
            _isSubscribed = true;
        }
    }

    // Close: unsubscribe events to prevent memory leaks and useless background rendering
    protected override void OnClose()
    {
        if (_isSubscribed)
        {
            if (_viewModelHandler != null)
                ChatViewModel.Instance.Unsubscribe(_viewModelHandler);
            _viewModelHandler = null;
            _isSubscribed = false;
        }

        Debug.Log("OnClose");
    }

    // Cleanup on destroy
    public override void OnDestroy()
    {
        // Unregister input callbacks to avoid leaks
        if (_chatInput != null)
            _chatInput.UnregisterCallback<KeyDownEvent>(OnChatInputKeyDown);
        base.OnDestroy();
    }

    // Handle Enter key in the chat input: send message and clear input
    private void OnChatInputKeyDown(KeyDownEvent evt)
    {
        if (evt == null) return;

        // Check for Return / Enter keys
        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
        {
            var text = _chatInput.value?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                // Use ViewModel to process player input: records history, triggers UI updates, forwards to McpAsyncClient
                ChatViewModel.Instance.AddPlayerMessage(text);
                _chatInput.value = string.Empty;
            }

            // Prevent further processing of the Enter key
            evt.StopImmediatePropagation();
        }
    }

    // Instantiate a VisualTreeAsset template, set the label named "message-text" and add it to the scroll view
    private void AddMessageFromTemplate(VisualTreeAsset template, string messageText)
    {
        if (template == null)
        {
            Debug.LogWarning("ChatWindow: template is null, cannot add message.");
            return;
        }

        if (_chatScrollView == null)
        {
            Debug.LogWarning("ChatWindow: _chatScrollView is null, cannot add message.");
            return;
        }

        // Clone the template into a new element tree
        var container = template.CloneTree();
        if (container == null)
        {
            Debug.LogWarning("ChatWindow: failed to clone template tree.");
            return;
        }

        // Find the label inside the cloned tree and set text
        var label = container.Q<Label>("message-text");
        if (label != null)
        {
            label.text = messageText;
        }
        else
        {
            // If no named label, try to find the first Label child
            var firstLabel = container.Q<Label>();
            if (firstLabel != null)
                firstLabel.text = messageText;
            else
                Debug.LogWarning("ChatWindow: could not find a Label in the cloned message template to set text.");
        }
        
        _chatScrollView.Add(container);
        _chatScrollView.ScrollTo(container);
    }

    public enum Role
    {
        Player,
        Opponent,
        System
    }
    
    // Pure UI render method (only renders, no ViewModel routing)
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
