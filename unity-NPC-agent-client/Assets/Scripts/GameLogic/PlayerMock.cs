using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player mock that holds a list of NPCs in the scene, opens the ChatWindow on F,
/// and manages the ChatWindow lifecycle. Uses Unity's new InputSystem.
/// </summary>
public class PlayerMock : MonoBehaviour
{
    [Header("NPC 引用")]
    [Tooltip("场景中所有 NPC 实体的列表")]
    public List<NpcEntity> npcEntities = new List<NpcEntity>();

    // Whether we are currently in UI mode (chat is open)
    private bool _isUiMode;
    private ChatWindow _chatWindow;

    // ── Gameplay input ────────────────────────────────────
    private InputAction _interactAction;
    private System.Action<InputAction.CallbackContext> _onInteract;

    // ── UI input ──────────────────────────────────────────
    private InputAction _closeChatAction;
    private System.Action<InputAction.CallbackContext> _onCloseChat;

    // ──────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────

    private void Awake()
    {
        _interactAction = new InputAction("Interact", binding: "<Keyboard>/f");
        _closeChatAction = new InputAction("CloseChat", binding: "<Keyboard>/escape");

        _onInteract = _ => OnInteract();
        _onCloseChat = _ => OnCloseChat();

        _interactAction.performed += _onInteract;
        _closeChatAction.performed += _onCloseChat;

        if (npcEntities == null)
            npcEntities = new List<NpcEntity>();
    }

    private void OnEnable()
    {
        UpdateInputState();
    }

    private void OnDisable()
    {
        CleanupChatWindow();

        if (_interactAction != null)
            _interactAction.Disable();
        if (_closeChatAction != null)
            _closeChatAction.Disable();
    }

    private void OnDestroy()
    {
        if (_interactAction != null)
        {
            _interactAction.performed -= _onInteract;
            _interactAction.Dispose();
            _interactAction = null;
            _onInteract = null;
        }
        if (_closeChatAction != null)
        {
            _closeChatAction.performed -= _onCloseChat;
            _closeChatAction.Dispose();
            _closeChatAction = null;
            _onCloseChat = null;
        }
    }

    // ──────────────────────────────────────────────────────
    //  Mode switching
    // ──────────────────────────────────────────────────────

    private void UpdateInputState()
    {
        if (_isUiMode)
        {
            _interactAction?.Disable();
            _closeChatAction?.Enable();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            _closeChatAction?.Disable();
            _interactAction?.Enable();
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    private void SwitchToUiMode()
    {
        _isUiMode = true;
        UpdateInputState();
    }

    private void SwitchToGameplayMode()
    {
        _isUiMode = false;
        UpdateInputState();
    }

    // ──────────────────────────────────────────────────────
    //  Input callbacks
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// F key — open chat window with all NPCs available for conversation.
    /// </summary>
    private void OnInteract()
    {
        if (npcEntities == null || npcEntities.Count == 0)
        {
            Debug.LogWarning("PlayerMock: 场景中没有 NPC，无法打开对话。");
            return;
        }

        if (UIManager.Instance == null)
        {
            Debug.LogWarning("PlayerMock: UIManager.Instance 不可用。");
            return;
        }

        if (_chatWindow == null)
        {
            _chatWindow = UIManager.Instance.OpenNewWindow<ChatWindow>();
            _chatWindow.Closed += OnChatWindowClosed;
        }
        else if (!_chatWindow.IsOpen)
        {
            UIManager.Instance.ReopenWindow(_chatWindow);
        }

        // 将 NPC 列表推送到 ChatViewModel
        var npcIds = new List<string>();
        foreach (var npc in npcEntities)
        {
            if (npc != null && !string.IsNullOrWhiteSpace(npc.npcId))
                npcIds.Add(npc.npcId);
        }
        ChatViewModel.Instance.SetNpcList(npcIds);

        // 如果没有活跃 NPC，自动选择第一个
        if (string.IsNullOrWhiteSpace(ChatViewModel.Instance.ActiveNpcId) && npcIds.Count > 0)
            ChatViewModel.Instance.SelectNpc(npcIds[0]);

        SwitchToUiMode();
    }

    /// <summary>
    /// ESC key — close the chat window and return to gameplay mode.
    /// </summary>
    private void OnCloseChat()
    {
        if (_chatWindow != null && _chatWindow.IsOpen)
        {
            _chatWindow.Close();
        }
        else
        {
            SwitchToGameplayMode();
        }
    }

    private void OnChatWindowClosed()
    {
        SwitchToGameplayMode();
    }

    private void CleanupChatWindow()
    {
        if (_chatWindow != null)
        {
            _chatWindow.Closed -= OnChatWindowClosed;
            _chatWindow = null;
        }
    }
}
