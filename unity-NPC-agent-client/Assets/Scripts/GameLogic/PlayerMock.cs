using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player mock that handles input for gameplay (F → interact) and UI (ESC → close chat).
/// Uses Unity's new InputSystem with InputAction created in code.
/// </summary>
public class PlayerMock : MonoBehaviour
{
    public NpcEntity targetNpc;

    // Whether we are currently in UI mode (chat is open)
    private bool _isUiMode;

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
        // Create actions but don't enable yet — OnEnable handles that
        _interactAction = new InputAction("Interact", binding: "<Keyboard>/f");
        _closeChatAction = new InputAction("CloseChat", binding: "<Keyboard>/escape");

        _onInteract = _ => OnInteract();
        _onCloseChat = _ => OnCloseChat();

        _interactAction.performed += _onInteract;
        _closeChatAction.performed += _onCloseChat;
    }

    private void OnEnable()
    {
        if (targetNpc != null)
            targetNpc.InteractionEnded += SwitchToGameplayMode;

        UpdateInputState();
    }

    private void OnDisable()
    {
        if (targetNpc != null)
            targetNpc.InteractionEnded -= SwitchToGameplayMode;

        if (_interactAction != null)
        {
            _interactAction.Disable();
        }
        if (_closeChatAction != null)
        {
            _closeChatAction.Disable();
        }
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
            // UI mode: only ESC is active
            _interactAction?.Disable();
            _closeChatAction?.Enable();
        }
        else
        {
            // Gameplay mode: only F is active
            _closeChatAction?.Disable();
            _interactAction?.Enable();
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
    /// F key — interact with the target NPC, then switch to UI mode.
    /// </summary>
    private void OnInteract()
    {
        // Edge case: no target NPC assigned
        if (targetNpc == null)
        {
            Debug.LogWarning("PlayerMock: targetNpc is null, cannot interact.");
            return;
        }

        targetNpc.Interact();
        SwitchToUiMode();
    }

    /// <summary>
    /// ESC key — close the chat window if open, then switch back to gameplay mode.
    /// </summary>
    private void OnCloseChat()
    {
        // Notify the NPC to stop interaction (closes chat, resets state)
        if (targetNpc != null)
        {
            targetNpc.StopInteract();
        }
        else
        {
            // Fallback: close chat directly if no NPC reference
            if (UIManager.Instance != null)
            {
                // var chatWindow = UIManager.Instance.GetWindow<ChatWindow>();
                // chatWindow?.Close();
            }
        }

        SwitchToGameplayMode();
    }
}
