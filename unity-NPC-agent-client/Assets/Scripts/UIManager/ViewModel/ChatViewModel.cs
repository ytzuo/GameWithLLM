using System;
using System.Collections.Generic;
using UnityEngine;

/*
 ChatViewModel 的职责：
 - 保存每个 NPC 独立的聊天记录（per-NPC histories）
 - 管理 NPC 列表和当前活跃 NPC
 - 对外通过事件通知 UI（与 ChatWindow.AddMessageToUI 的签名兼容）
 - 处理来自 UI 的输入（把玩家输入分发到 A2A 会话适配层）

 说明：项目中存在 ChatWindow.AddMessageToUI(ChatWindow.Role, string)，
 因此这里的事件使用相同签名 Action<ChatWindow.Role, string> 方便直接订阅。
 多 NPC 支持：OnMessageAdded 只针对当前活跃 NPC 触发，切换 NPC 时通过
 OnActiveNpcChanged 通知 UI 刷新整个历史。
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

	// ── 多 NPC 状态 ────────────────────────────────────────

	private readonly Dictionary<string, List<Message>> _npcHistories =
		new Dictionary<string, List<Message>>();
	private readonly Dictionary<string, int> _streamingOpponentMessageIndexes =
		new Dictionary<string, int>();
	private List<string> _npcIds = new List<string>();
	private string _activeNpcId;

	/// <summary>
	/// 当前活跃的 NPC ID（null 表示尚未选择）
	/// </summary>
	public string ActiveNpcId
	{
		get
		{
			lock (_lock) return _activeNpcId;
		}
	}

	/// <summary>
	/// 当前已知的 NPC ID 列表
	/// </summary>
	public IReadOnlyList<string> NpcIds
	{
		get
		{
			lock (_lock) return _npcIds.AsReadOnly();
		}
	}

	// ── 事件 ───────────────────────────────────────────────

	/// <summary>
	/// 与 ChatWindow.AddMessageToUI 签名兼容，UI 层可直接订阅。
	/// 仅针对当前活跃 NPC 触发。
	/// </summary>
	public event Action<ChatWindow.Role, string> OnMessageAdded;
	public event Action<ChatWindow.Role, string> OnMessageUpdated;
	public event Action OnHistoryChanged;

	/// <summary>
	/// NPC 列表发生变化时触发（新增/移除 NPC）
	/// </summary>
	public event Action<List<string>> OnNpcListChanged;

	/// <summary>
	/// 活跃 NPC 切换时触发，UI 应清空聊天区域并重新加载历史。
	/// </summary>
	public event Action<string> OnActiveNpcChanged;

	// ── NPC 列表管理 ───────────────────────────────────────

	/// <summary>
	/// 由 PlayerMock 调用，设置当前场景中的 NPC 列表。
	/// </summary>
	public void SetNpcList(List<string> npcIds)
	{
		if (npcIds == null) npcIds = new List<string>();

		lock (_lock)
		{
			_npcIds = new List<string>(npcIds);
		}

		try { OnNpcListChanged?.Invoke(new List<string>(npcIds)); }
		catch (Exception e) { Debug.LogWarning($"ChatViewModel: OnNpcListChanged error: {e.Message}"); }
	}

	/// <summary>
	/// 切换活跃 NPC。若 NPC 不存在则忽略。
	/// </summary>
	public void SelectNpc(string npcId)
	{
		if (string.IsNullOrWhiteSpace(npcId)) return;

		lock (_lock)
		{
			if (!_npcIds.Contains(npcId))
			{
				Debug.LogWarning($"ChatViewModel: NPC '{npcId}' is not in the known NPC list.");
				return;
			}
			if (_activeNpcId == npcId) return;
			_activeNpcId = npcId;
		}

		// 通知场景门面切换当前交互实体；A2A Context 由适配层按实体复用。
		try
		{
			AgentHostClient.Instance.OnPlayerInteractWithNpc(npcId);
		}
		catch (Exception e)
		{
			Debug.LogWarning($"ChatViewModel: failed to notify AgentHostClient: {e.Message}");
		}

		try { OnActiveNpcChanged?.Invoke(npcId); }
		catch (Exception e) { Debug.LogWarning($"ChatViewModel: OnActiveNpcChanged error: {e.Message}"); }
	}

	// ── 公共 API：添加消息 ─────────────────────────────────

	/// <summary>
	/// 添加一条玩家消息到当前活跃 NPC 的历史中。
	/// </summary>
	public void AddPlayerMessage(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		string npcId;
		lock (_lock) npcId = _activeNpcId;
		if (string.IsNullOrWhiteSpace(npcId)) return;

		var msg = new Message(ChatWindow.Role.Player, text);
		AddToHistory(npcId, msg);
		Notify(msg.Role, msg.Text);

		// 将玩家输入提交到会话层
		try
		{
			AgentHostClient.Instance.SubmitPlayerInput(text);
		}
		catch (Exception e)
		{
			Debug.LogWarning($"ChatViewModel: failed to forward player input to AgentHostClient: {e.Message}");
		}
	}

	/// <summary>
	/// 添加一条 NPC 回复消息到指定 NPC 的历史中。
	/// 仅当该 NPC 是当前活跃 NPC 时才通知 UI。
	/// </summary>
	public void AddOpponentMessage(string npcId, string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		if (string.IsNullOrWhiteSpace(npcId)) return;

		var msg = new Message(ChatWindow.Role.Opponent, text);
		AddToHistory(npcId, msg);

		string activeNpc;
		lock (_lock) activeNpc = _activeNpcId;
		if (activeNpc == npcId)
			Notify(msg.Role, msg.Text);
	}

	/// <summary>
	/// 将模型增量追加到当前 NPC 的同一条回复中。
	/// </summary>
	public void AppendOpponentMessageDelta(string npcId, string delta)
	{
		if (string.IsNullOrEmpty(delta) || string.IsNullOrWhiteSpace(npcId)) return;

		Message message;
		bool added;
		bool isActive;
		lock (_lock)
		{
			if (!_npcHistories.TryGetValue(npcId, out var history))
			{
				history = new List<Message>();
				_npcHistories[npcId] = history;
			}

			if (_streamingOpponentMessageIndexes.TryGetValue(npcId, out int index) &&
				index >= 0 && index < history.Count)
			{
				message = history[index];
				message.Text += delta;
				message.Timestamp = DateTime.UtcNow;
				history[index] = message;
				added = false;
			}
			else
			{
				message = new Message(ChatWindow.Role.Opponent, delta);
				history.Add(message);
				_streamingOpponentMessageIndexes[npcId] = history.Count - 1;
				added = true;
			}
			isActive = _activeNpcId == npcId;
		}

		if (!isActive) return;
		if (added)
			Notify(message.Role, message.Text);
		else
			NotifyUpdated(message.Role, message.Text);
	}

	/// <summary>
	/// 结束一条流式回复；未收到增量时使用最终文本创建普通消息。
	/// </summary>
	public void CompleteOpponentMessageStream(string npcId, string finalText)
	{
		if (string.IsNullOrWhiteSpace(npcId)) return;

		bool hadStream;
		lock (_lock)
			hadStream = _streamingOpponentMessageIndexes.Remove(npcId);

		if (!hadStream && !string.IsNullOrWhiteSpace(finalText))
			AddOpponentMessage(npcId, finalText);
	}

	public void CancelOpponentMessageStream(string npcId)
	{
		if (string.IsNullOrWhiteSpace(npcId)) return;

		bool removed = false;
		bool isActive;
		lock (_lock)
		{
			if (_streamingOpponentMessageIndexes.TryGetValue(npcId, out int index) &&
				_npcHistories.TryGetValue(npcId, out var history) &&
				index >= 0 && index < history.Count)
			{
				history.RemoveAt(index);
				removed = true;
			}
			_streamingOpponentMessageIndexes.Remove(npcId);
			isActive = _activeNpcId == npcId;
		}

		if (removed && isActive)
		{
			try { OnHistoryChanged?.Invoke(); }
			catch (Exception e) { Debug.LogWarning($"ChatViewModel: OnHistoryChanged error: {e.Message}"); }
		}
	}

	/// <summary>
	/// 添加一条系统消息到指定 NPC 的历史中。
	/// 仅当该 NPC 是当前活跃 NPC 时才通知 UI。
	/// </summary>
	public void AddSystemMessage(string npcId, string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return;
		if (string.IsNullOrWhiteSpace(npcId)) return;

		var msg = new Message(ChatWindow.Role.System, text);
		AddToHistory(npcId, msg);

		string activeNpc;
		lock (_lock) activeNpc = _activeNpcId;
		if (activeNpc == npcId)
			Notify(msg.Role, msg.Text);
	}

	/// <summary>
	/// 清理活跃 NPC 的历史。
	/// </summary>
	public void ClearActiveHistory()
	{
		string npcId;
		lock (_lock) npcId = _activeNpcId;
		if (string.IsNullOrWhiteSpace(npcId)) return;

		lock (_lock)
		{
			if (_npcHistories.TryGetValue(npcId, out var list))
				list.Clear();
			_streamingOpponentMessageIndexes.Remove(npcId);
		}
	}

	/// <summary>
	/// 清理所有 NPC 的历史。
	/// </summary>
	public void ClearAllHistory()
	{
		lock (_lock)
		{
			_npcHistories.Clear();
			_streamingOpponentMessageIndexes.Clear();
		}
	}

	/// <summary>
	/// 用 Go 加载结果整体替换所有 NPC 的 UI 可见历史，不与旧世界消息合并。
	/// </summary>
	public void ReplaceHistories(IReadOnlyList<AgentLoadedConversationContext> contexts)
	{
		lock (_lock)
		{
			_npcHistories.Clear();
			_streamingOpponentMessageIndexes.Clear();
			if (contexts != null)
			{
				foreach (AgentLoadedConversationContext context in contexts)
				{
					if (context == null || string.IsNullOrWhiteSpace(context.NpcId)) continue;
					var history = new List<Message>();
					if (context.VisibleMessages != null)
					{
						foreach (AgentVisibleMessage visible in context.VisibleMessages)
						{
							if (visible == null || string.IsNullOrWhiteSpace(visible.Text)) continue;
							ChatWindow.Role role = string.Equals(visible.Role, "user", StringComparison.Ordinal)
								? ChatWindow.Role.Player
								: ChatWindow.Role.Opponent;
							history.Add(new Message(role, visible.Text));
						}
					}
					_npcHistories[context.NpcId] = history;
				}
			}
		}
		try { OnHistoryChanged?.Invoke(); }
		catch (Exception e) { Debug.LogWarning($"ChatViewModel: OnHistoryChanged error: {e.Message}"); }
	}
	// ── 历史同步 ───────────────────────────────────────────

	/// <summary>
	/// 将当前活跃 NPC 的历史一次性推送给 UI（用于窗口打开 / NPC 切换时同步）。
	/// </summary>
	public void PopulateExistingHistory(Action<ChatWindow.Role, string> addMessageAction)
	{
		if (addMessageAction == null) return;
		List<Message> history;
		lock (_lock)
		{
			string npcId = _activeNpcId;
			if (string.IsNullOrWhiteSpace(npcId) || !_npcHistories.TryGetValue(npcId, out history))
				return;
			history = new List<Message>(history); // snapshot
		}
		foreach (var m in history)
			addMessageAction(m.Role, m.Text);
	}

	// ── 订阅 / 退订 ────────────────────────────────────────

	public void Subscribe(Action<ChatWindow.Role, string> handler) => OnMessageAdded += handler;
	public void Unsubscribe(Action<ChatWindow.Role, string> handler) => OnMessageAdded -= handler;
	public void SubscribeToUpdates(Action<ChatWindow.Role, string> handler) => OnMessageUpdated += handler;
	public void UnsubscribeFromUpdates(Action<ChatWindow.Role, string> handler) => OnMessageUpdated -= handler;

	// ── 内部帮助 ───────────────────────────────────────────

	private void AddToHistory(string npcId, Message msg)
	{
		lock (_lock)
		{
			if (!_npcHistories.TryGetValue(npcId, out var list))
			{
				list = new List<Message>();
				_npcHistories[npcId] = list;
			}
			list.Add(msg);
		}
	}

	private void Notify(ChatWindow.Role role, string text)
	{
		try { OnMessageAdded?.Invoke(role, text); }
		catch (Exception e) { Debug.LogWarning($"ChatViewModel: OnMessageAdded error: {e.Message}"); }
	}

	private void NotifyUpdated(ChatWindow.Role role, string text)
	{
		try { OnMessageUpdated?.Invoke(role, text); }
		catch (Exception e) { Debug.LogWarning($"ChatViewModel: OnMessageUpdated error: {e.Message}"); }
	}
}
