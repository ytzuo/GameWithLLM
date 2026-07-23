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

    public event Action<string, bool> NpcChanged;

    public void RegisterNpc(string id, NpcEntity npc)
    {
        if (string.IsNullOrWhiteSpace(id) || npc == null)
            return;
        lock (_npcLock)
            _npcEntities[id] = npc;
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
            return new List<string>(_npcEntities.Keys);
    }

    public void OnReceiveNetMessage(UnityToolCommand request)
    {
        _netIncomingQueue.Enqueue(request);
    }

    public void CancelRequest(string requestId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
            _cancelledRequests[requestId] = 0;
    }
    private void Update()
    {
        while (_netIncomingQueue.TryDequeue(out UnityToolCommand request))
        {
            if (!string.IsNullOrEmpty(request.RequestId) && _cancelledRequests.TryRemove(request.RequestId, out _))
                continue;
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
                }
            }
        }
    }

}
