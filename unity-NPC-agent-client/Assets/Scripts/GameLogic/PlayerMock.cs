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

    // ── Inventory UI ──────────────────────────────────────
    private InventoryWindow _inventoryWindow;
    private InventoryListWindow _inventoryListWindow;
    private InventoryInteractWindow _inventoryInteractWindow;
    private InputAction _inventoryAction; // Tab key
    private InputAction _openInventoryAction; // E key

    // ── Item Dispenser ──
    public ItemDataList itemDataList; // set in Inspector
    private ItemDispenserWindow _dispenserWindow;
    private InputAction _dispenserAction; // D key

    // ──────────────────────────────────────────────────────
    //  Lifecycle
    // ──────────────────────────────────────────────────────

    private void Awake()
    {
        _interactAction = new InputAction("Interact", binding: "<Keyboard>/f");
        _closeChatAction = new InputAction("CloseChat", binding: "<Keyboard>/escape");

        // Tab —— 打开物品栏列表
        _inventoryAction = new InputAction("Inventory", InputActionType.Button, "<Keyboard>/tab", null, null, null);

        // E —— 打开玩家背包
        _openInventoryAction = new InputAction("OpenInventory", InputActionType.Button, "<Keyboard>/e", null, null, null);

        // D —— 打开物品发放器
        _dispenserAction = new InputAction("Dispenser", InputActionType.Button, "<Keyboard>/d", null, null, null);

        _onInteract = _ => OnInteract();
        _onCloseChat = _ => OnCloseChat();

        _interactAction.performed += _onInteract;
        _closeChatAction.performed += _onCloseChat;
        _inventoryAction.performed += OnInventoryToggle;
        _openInventoryAction.performed += OnOpenInventory;
        _dispenserAction.performed += OnOpenDispenser;

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
        CleanupInventoryWindows();

        if (_interactAction != null)
            _interactAction.Disable();
        if (_closeChatAction != null)
            _closeChatAction.Disable();
        if (_inventoryAction != null)
            _inventoryAction.Disable();
        if (_openInventoryAction != null)
            _openInventoryAction.Disable();
        if (_dispenserAction != null)
            _dispenserAction.Disable();
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
        if (_inventoryAction != null)
        {
            _inventoryAction.performed -= OnInventoryToggle;
            _inventoryAction.Dispose();
            _inventoryAction = null;
        }
        if (_openInventoryAction != null)
        {
            _openInventoryAction.performed -= OnOpenInventory;
            _openInventoryAction.Dispose();
            _openInventoryAction = null;
        }
        if (_dispenserAction != null)
        {
            _dispenserAction.performed -= OnOpenDispenser;
            _dispenserAction.Dispose();
            _dispenserAction = null;
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
            _inventoryAction?.Disable();
            _openInventoryAction?.Disable();
            _dispenserAction?.Disable();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        else
        {
            _closeChatAction?.Disable();
            _interactAction?.Enable();
            _inventoryAction?.Enable();
            _openInventoryAction?.Enable();
            _dispenserAction?.Enable();
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
        // 关闭物品栏窗口（如果打开）
        if (_inventoryWindow != null && _inventoryWindow.IsOpen)
        {
            _inventoryWindow.Close();
        }
        if (_inventoryInteractWindow != null && _inventoryInteractWindow.IsOpen)
        {
            _inventoryInteractWindow.Close();
        }
        if (_inventoryListWindow != null && _inventoryListWindow.IsOpen)
        {
            _inventoryListWindow.Close();
        }
        if (_dispenserWindow != null && _dispenserWindow.IsOpen)
        {
            _dispenserWindow.Close();
        }

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

    // ──────────────────────────────────────────────────────
    //  Inventory UI
    // ──────────────────────────────────────────────────────

    private void OnInventoryToggle(InputAction.CallbackContext ctx)
    {
        // 已经在物品栏模式中则忽略
        if (_inventoryListWindow != null && _inventoryListWindow.IsOpen) return;
        if (_inventoryInteractWindow != null && _inventoryInteractWindow.IsOpen) return;

        // 获取玩家物品栏组件
        var playerInventory = GetComponent<InventoryComponent>();
        if (playerInventory == null)
        {
            Debug.LogWarning("PlayerMock: 玩家没有 InventoryComponent，无法打开物品栏");
            return;
        }

        // 登记玩家物品栏
        InventoryViewModel.Instance.PlayerInventory = playerInventory;

        // 切换到 UI 模式（如果还没切）
        if (!_isUiMode) SwitchToUiMode();

        // 打开容器列表
        _inventoryListWindow = UIManager.Instance.OpenNewWindow<InventoryListWindow>();
        _inventoryListWindow.Closed += OnInventoryListClosed;
        _inventoryListWindow.OnContainerSelected += OnContainerSelected;
    }

    private void OnOpenInventory(InputAction.CallbackContext ctx)
    {
        // Guard: don't open if already open
        if (_inventoryWindow != null && _inventoryWindow.IsOpen) return;

        var playerInventory = GetComponent<InventoryComponent>();
        if (playerInventory == null)
        {
            Debug.LogWarning("PlayerMock: 玩家没有 InventoryComponent，无法打开背包");
            return;
        }

        // Switch to UI mode if not already
        if (!_isUiMode) SwitchToUiMode();

        // Open inventory window
        _inventoryWindow = UIManager.Instance.OpenNewWindow<InventoryWindow>();
        _inventoryWindow.Inventory = playerInventory;
        _inventoryWindow.Closed += OnInventoryWindowClosed;
    }

    // ── Item Dispenser ────────────────────────────────────

    private void OnOpenDispenser(InputAction.CallbackContext ctx)
    {
        if (_dispenserWindow != null && _dispenserWindow.IsOpen) return;

        if (itemDataList == null)
        {
            Debug.LogWarning("PlayerMock: itemDataList 未设置，无法打开物品发放器");
            return;
        }

        if (!_isUiMode) SwitchToUiMode();

        _dispenserWindow = UIManager.Instance.OpenNewWindow<ItemDispenserWindow>();
        _dispenserWindow.ItemDataList = itemDataList;
        _dispenserWindow.Closed += OnDispenserWindowClosed;
    }

    private void OnDispenserWindowClosed()
    {
        if (_dispenserWindow != null)
        {
            _dispenserWindow.Closed -= OnDispenserWindowClosed;
            _dispenserWindow = null;
        }
        SwitchToGameplayMode();
    }

    private void OnInventoryWindowClosed()
    {
        if (_inventoryWindow != null)
        {
            _inventoryWindow.Closed -= OnInventoryWindowClosed;
            _inventoryWindow = null;
        }
        SwitchToGameplayMode();
    }

    private void OnContainerSelected(InventoryComponent targetContainer)
    {
        if (targetContainer == null) return;

        // 关闭容器列表
        if (_inventoryListWindow != null)
        {
            _inventoryListWindow.OnContainerSelected -= OnContainerSelected;
            _inventoryListWindow.Closed -= OnInventoryListClosed;
            _inventoryListWindow.Close();
            _inventoryListWindow = null;
        }

        // 打开交互窗口
        var playerInventory = GetComponent<InventoryComponent>();
        if (playerInventory == null) return;

        string targetName = InventoryViewModel.Instance.GetContainerName(targetContainer);
        _inventoryInteractWindow = UIManager.Instance.OpenNewWindow<InventoryInteractWindow>();
        _inventoryInteractWindow.Closed += OnInventoryInteractClosed;
        _inventoryInteractWindow.SetInventories(playerInventory, targetContainer, targetName);
    }

    private void OnInventoryListClosed()
    {
        if (_inventoryListWindow != null)
        {
            _inventoryListWindow.OnContainerSelected -= OnContainerSelected;
            _inventoryListWindow.Closed -= OnInventoryListClosed;
            _inventoryListWindow = null;
        }
        SwitchToGameplayMode();
    }

    private void OnInventoryInteractClosed()
    {
        if (_inventoryInteractWindow != null)
        {
            _inventoryInteractWindow.Closed -= OnInventoryInteractClosed;
            _inventoryInteractWindow = null;
        }
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

    private void CleanupInventoryWindows()
    {
        if (_inventoryWindow != null)
        {
            _inventoryWindow.Closed -= OnInventoryWindowClosed;
            _inventoryWindow = null;
        }
        if (_inventoryListWindow != null)
        {
            _inventoryListWindow.OnContainerSelected -= OnContainerSelected;
            _inventoryListWindow.Closed -= OnInventoryListClosed;
            _inventoryListWindow = null;
        }
        if (_inventoryInteractWindow != null)
        {
            _inventoryInteractWindow.Closed -= OnInventoryInteractClosed;
            _inventoryInteractWindow = null;
        }
        if (_dispenserWindow != null)
        {
            _dispenserWindow.Closed -= OnDispenserWindowClosed;
            _dispenserWindow = null;
        }
    }
}
