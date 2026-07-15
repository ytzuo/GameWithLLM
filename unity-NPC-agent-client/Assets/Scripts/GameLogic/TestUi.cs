using UnityEngine;
using UnityEngine.InputSystem;

namespace GameLogic
{
    public class TestUi : MonoBehaviour
    {
        // InputAction created in code and bound to the Keyboard Space key.
        private InputAction _openChatAction;
        private System.Action<InputAction.CallbackContext> _onOpenChat;

        private void OnEnable()
        {
            // Create a simple button action bound to the space key.
            _openChatAction = new InputAction(binding: "<Keyboard>/space");
            _onOpenChat = _ => OpenChat();
            _openChatAction.performed += _onOpenChat;
            _openChatAction.Enable();
        }

        private void OnDisable()
        {
            if (_openChatAction != null)
            {
                _openChatAction.performed -= _onOpenChat;
                _openChatAction.Disable();
                _openChatAction.Dispose();
                _openChatAction = null;
                _onOpenChat = null;
            }
        }

        private void OpenChat()
        {
            UIManager.Instance.OpenWindow(WindowID.Chat);
        }
    }

}