using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

public class CommandDispatcher : Singleton<CommandDispatcher>
{
    private readonly Dictionary<string, NpcEntity> _npcEntities = new Dictionary<string, NpcEntity>();
    private readonly object _npcLock = new object();
    private readonly ConcurrentQueue<UnityToolCommand> _netIncomingQueue = new ConcurrentQueue<UnityToolCommand>();
    private readonly ConcurrentDictionary<string, byte> _cancelledRequests = new ConcurrentDictionary<string, byte>();
    private readonly ConcurrentDictionary<string, byte> _activeRequests = new ConcurrentDictionary<string, byte>();

    public event Action<string, bool> NpcChanged;
    public event Action<string> NpcCapabilitiesChanged;

    public void RegisterNpc(string id, NpcEntity npc)
    {
        if (string.IsNullOrWhiteSpace(id) || npc == null)
            return;
        lock (_npcLock)
        {
            if (_npcEntities.TryGetValue(id, out NpcEntity existing) && existing != npc)
            {
                Debug.LogError(
                    $"[Router] NPC ID 重复：'{id}' 已由 '{existing.gameObject.name}' 注册，" +
                    $"无法再注册 '{npc.gameObject.name}'。",
                    npc);
                return;
            }
            _npcEntities[id] = npc;
        }
        NpcChanged?.Invoke(id, true);
    }

    public void UnregisterNpc(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        lock (_npcLock)
        {
            if (!_npcEntities.Remove(id))
                return;
        }
        NpcChanged?.Invoke(id, false);
    }

    public List<string> GetRegisteredNpcIds()
    {
        lock (_npcLock)
        {
            var ids = new List<string>(_npcEntities.Keys);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
    }

    public Dictionary<string, NpcEntity> GetRegisteredNpcsSnapshot()
    {
        lock (_npcLock)
            return new Dictionary<string, NpcEntity>(_npcEntities);
    }

    public void NotifyNpcCapabilitiesChanged(string id, NpcEntity npc)
    {
        if (string.IsNullOrWhiteSpace(id) || npc == null)
            return;
        lock (_npcLock)
        {
            if (!_npcEntities.TryGetValue(id, out NpcEntity registered) || registered != npc)
                return;
        }
        NpcCapabilitiesChanged?.Invoke(id);
    }

    public void OnReceiveNetMessage(UnityToolCommand request)
    {
        if (!string.IsNullOrWhiteSpace(request?.RequestId))
            _activeRequests[request.RequestId] = 0;
        _netIncomingQueue.Enqueue(request);
    }

    public void CancelRequest(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId) && _activeRequests.ContainsKey(requestId))
            _cancelledRequests[requestId] = 0;
    }

    public bool IsCancellationRequested(string requestId)
    {
        return !string.IsNullOrWhiteSpace(requestId) && _cancelledRequests.ContainsKey(requestId);
    }

    public void CompleteRequest(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return;
        _activeRequests.TryRemove(requestId, out _);
        _cancelledRequests.TryRemove(requestId, out _);
    }

    private void Update()
    {
        while (_netIncomingQueue.TryDequeue(out UnityToolCommand request))
        {
            if (!string.IsNullOrEmpty(request.RequestId) && IsCancellationRequested(request.RequestId))
            {
                CompleteRequest(request.RequestId);
                continue;
            }

            NpcEntity npc;
            lock (_npcLock)
                _npcEntities.TryGetValue(request.NpcId, out npc);
            if (npc != null)
            {
                npc.ReceiveCommand(request);
            }
            else
            {
                Debug.LogWarning($"[Router] 收到 {request.NpcId} 的命令，但该 NPC 实体不存在。");
                if (!string.IsNullOrEmpty(request.RequestId))
                {
                    _ = AgentHostClient.Instance.SendToolResponseAsync(
                        request.RequestId,
                        $"NPC '{request.NpcId}' 未注册或已离线。",
                        true,
                        "NPC_NOT_FOUND");
                    CompleteRequest(request.RequestId);
                }
            }
        }
    }
}
