using System;
using System.Collections.Generic;
using UnityEngine;

/*
 ChatViewModel 的职责：
 - 保存聊天记录（MessageHistory）
 - 对外通过事件通知 UI（与 ChatWindow.AddMessageToUI 的签名兼容）
 - 处理来自 UI 的输入（把玩家输入分发到Go Agent Host 发送到会话层）

 说明：项目中存在 ChatWindow.AddMessageToUI(ChatWindow.Role, string)，
 因此这里的事件使用相同签名 Action<ChatWindow.Role, string> 方便直接订阅。
*/
public class ChatViewModel
{
	// 单例实现（非-MonoBehaviour）方便全局访问
	private static ChatViewModel _instance;
	public static ChatViewModel Instance => _instance ?? (_instance = new ChatViewModel());
	private ChatViewModel() { }

	private readonly object _lock = new object();

	public struct Message
	{
		public ChatWindow.Role Role;
		public string Text;
		public DateTime Timestamp;

		public Message(ChatWindow.Role role, string text)
		{
			Role = role;
			Text = text;
			Timestamp = DateTime.UtcNow;
		}
	}

	// 保留最近会话的消息历史（简单实现，调用方可自行裁剪）
	private readonly List<Message> _messageHistory = new List<Message>();
	public IReadOnlyList<Message> MessageHistory
	{
		get
		{
			lock (_lock)
			{
				return _messageHistory.AsReadOnly();
			}
		}
	}

	// 与 ChatWindow.AddMessageToUI 签名兼容，UI 层可直接订阅
	public event Action<ChatWindow.Role, string> OnMessageAdded;

	// 公共 API：添加玩家消息（会触发事件并尝试提交到 Agent Host 会话层）
	public void AddPlayerMessage(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		var msg = new Message(ChatWindow.Role.Player, text);
		AddToHistoryAndNotify(msg);

		// 将玩家输入提交到会话层（如果 AgentHostClient 可用）
		try
		{
			// AgentHostClient 是项目中的单例客户端，用于将玩家输入转发给会话任务
			AgentHostClient.Instance.SubmitPlayerInput(text);
		}
		catch (Exception e)
		{
			Debug.LogWarning($"ChatViewModel: failed to forward player input to AgentHostClient: {e.Message}");
		}
	}

	public void AddOpponentMessage(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		var msg = new Message(ChatWindow.Role.Opponent, text);
		AddToHistoryAndNotify(msg);
	}

	public void AddSystemMessage(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		var msg = new Message(ChatWindow.Role.System, text);
		AddToHistoryAndNotify(msg);
	}

	// 清理历史（可选的容量限制/裁剪可由调用方实现）
	public void ClearHistory()
	{
		lock (_lock)
		{
			_messageHistory.Clear();
		}
	}

	// 订阅历史并同步（用于在窗口打开时一次性刷新旧消息）
	public void PopulateExistingHistory(Action<ChatWindow.Role, string> addMessageAction)
	{
		if (addMessageAction == null) return;
		lock (_lock)
		{
			foreach (var m in _messageHistory)
			{
				addMessageAction(m.Role, m.Text);
			}
		}
	}

	// 订阅/退订的便捷方法
	public void Subscribe(Action<ChatWindow.Role, string> handler)
	{
		OnMessageAdded += handler;
	}
	public void Unsubscribe(Action<ChatWindow.Role, string> handler)
	{
		OnMessageAdded -= handler;
	}

	// 内部帮助：保存并通知 UI
	private void AddToHistoryAndNotify(Message msg)
	{
		lock (_lock)
		{
			_messageHistory.Add(msg);
		}
		try
		{
			OnMessageAdded?.Invoke(msg.Role, msg.Text);
		}
		catch (Exception e)
		{
			Debug.LogWarning($"ChatViewModel: exception while notifying OnMessageAdded: {e.Message}");
		}
	}
}



