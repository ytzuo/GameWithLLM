using System.Collections.Generic;
using System.Collections.Concurrent;
using UnityEngine;

public class CommandDispatcher : Singleton<CommandDispatcher>
{
    // 管理全场景所有激活的 NPC 实体组件字典
    private readonly Dictionary<string, NpcEntity> _npcEntities = new Dictionary<string, NpcEntity>();

    // 全局唯一网络线程往这里塞数据
    private readonly ConcurrentQueue<LlmToolCall> _netIncomingQueue = new ConcurrentQueue<LlmToolCall>();

    public void RegisterNpc(string id, NpcEntity npc) => _npcEntities[id] = npc;
    public void UnregisterNpc(string id) => _npcEntities.Remove(id);

    // 全局唯一的 MCP 客户端网络线程接收到数据后，调用此方法（线程安全）
    public void OnReceiveNetMessage(LlmToolCall request)
    {
        _netIncomingQueue.Enqueue(request);
    }

    void Update()
    {
        // 主线程 Tick：高效分发包裹
        while (_netIncomingQueue.TryDequeue(out LlmToolCall request))
        {
            if (_npcEntities.TryGetValue(request.id, out NpcEntity npc))
            {
                // 定点投递到该 NPC 自己的状态机队列里，不影响其他 NPC
                npc.ReceiveCommand(request);
            }
            else
            {
                Debug.LogWarning($"[Router] 收到 {request.id} 的命令，但该NPC实体不存在。");
            }
        }
    }

    protected override void Init()
    {
        base.Init();

        // 在启动时注册场景级别的工具声明（Tools Discovery）
        // 为 MoveArgs 提供 JSON Schema，注意字段名需与 MoveArgs 保持一致
        string moveArgsSchema = @"{""type"": ""object"",
          ""properties"": {
            ""targetLandmark"": { ""type"": ""string"", ""enum"": [""warehouse"", ""gate""], ""description"": ""目标地标名称"" }
          },
          ""required"": [""targetLandmark""]
        }";

        ToolsRegistry.Instance.RegisterTool("game_npc_move", null, moveArgsSchema, "使 NPC 前往指定地标 (warehouse|gate)");
    }
}