using System;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.AI;

public class NpcEntity : MonoBehaviour
{
    public string npcId; // 在编辑器里写死，如 "Ryan_001"

    // 只属于我自己的任务轻量队列
    private readonly ConcurrentQueue<LlmToolCall> _myPrivateQueue = new ConcurrentQueue<LlmToolCall>();

    private NavMeshAgent _navAgent;
    private NpcState _fsmState = NpcState.Idle;

    public enum NpcState { Idle, Talking, Operating }

    void Start()
    {
        // 诞生时去全局路由报个到
        CommandDispatcher.Instance.RegisterNpc(npcId, this);
        _navAgent = GetComponent<NavMeshAgent>();
    }

    // 全局路由在主线程调用这个方法给我派活
    public void ReceiveCommand(LlmToolCall request)
    {
        _myPrivateQueue.Enqueue(request);
    }

    void Update()
    {
        // 纯正的单线程游戏 FSM 逻辑
        switch (_fsmState)
        {
            case NpcState.Idle:
                if (_myPrivateQueue.TryDequeue(out LlmToolCall request))
                {
                    ExecuteBusinessLogic(request);
                }
                break;

            case NpcState.Talking:
                if (!_navAgent.pathPending && _navAgent.remainingDistance <= _navAgent.stoppingDistance)
                {
                    _fsmState = NpcState.Idle;
                    // TODO: 行动完成，可以通过全局唯一的客户端把结果塞回网络，此处省略回传逻辑
                }
                break;
        }
    }

    private void ExecuteBusinessLogic(LlmToolCall request)
    {
        if (request.function.name == "game_npc_move")
        {
            // 利用上一问写的泛型拦截器进行安全的局部反序列化
            var wrapper = new McpToolWrapper<MoveArgs>((args) =>
            {
                GameObject landmark = GameObject.Find(args.targetLandmark);
                _navAgent.SetDestination(landmark.transform.position);
                return "NPC开始移动";
            });

            string result = wrapper.Execute(request.function.arguments);

            // 将执行结果原路返回给宿主（通过 MCP 客户端）
            if (!string.IsNullOrEmpty(request.transactionId))
            {
                // 异步发送响应，不阻塞主线程
                _ = McpAsyncClient.Instance.SendMcpResponseAsync(request.transactionId, result, false);
            }
        }
    }



    void OnDestroy() => CommandDispatcher.Instance.UnregisterNpc(npcId);
}
