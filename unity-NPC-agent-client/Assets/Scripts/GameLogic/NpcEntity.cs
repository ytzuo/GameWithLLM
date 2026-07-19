using System;
using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class NpcEntity : MonoBehaviour
{
    public string npcId;

    [Header("Movement landmarks")]
    [SerializeField] private Transform warehouseLandmark;
    [SerializeField] private Transform gateLandmark;

    private readonly ConcurrentQueue<LlmToolCall> _myPrivateQueue = new ConcurrentQueue<LlmToolCall>();
    private NavMeshAgent _navAgent;
    private NpcState _fsmState = NpcState.Idle;
    private ChatWindow _chatWindow;

    public event Action InteractionEnded;

    public enum NpcState { Idle, Talking, Operating }

    private void Start()
    {
        CommandDispatcher.Instance.RegisterNpc(npcId, this);
        _navAgent = GetComponent<NavMeshAgent>();

        if (_navAgent == null)
            Debug.LogError($"[NPC:{npcId}] NavMeshAgent is missing.", this);
    }

    public void Interact()
    {
        try
        {
            if (UIManager.Instance == null)
                return;

            if (_chatWindow == null)
            {
                _chatWindow = UIManager.Instance.OpenNewWindow<ChatWindow>();
                _chatWindow.Closed += OnChatWindowClosed;
                McpAsyncClient.Instance.OnPlayerInteractWithNpc(npcId);
            }
            else
            {
                UIManager.Instance.ReopenWindow(_chatWindow);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"NpcEntity.Interact failed: {ex.Message}");
        }
    }

    public void StopInteract()
    {
        _chatWindow?.Close();
        _fsmState = NpcState.Idle;
    }

    public void ReceiveCommand(LlmToolCall request)
    {
        _myPrivateQueue.Enqueue(request);
    }

    private void Update()
    {
        switch (_fsmState)
        {
            case NpcState.Idle:
                if (_myPrivateQueue.TryDequeue(out LlmToolCall request))
                    ExecuteBusinessLogic(request);
                break;

            case NpcState.Operating:
                if (_navAgent != null && !_navAgent.pathPending &&
                    (!_navAgent.hasPath || _navAgent.remainingDistance <= _navAgent.stoppingDistance))
                {
                    _fsmState = NpcState.Idle;
                }
                break;
        }
    }

    private void ExecuteBusinessLogic(LlmToolCall request)
    {
        if (request?.function?.name != "game_npc_move")
            return;

        var wrapper = new McpToolWrapper<MoveArgs>(MoveToLandmark);
        McpToolExecutionResult result = wrapper.Execute(request.function.arguments);

        Debug.Log(result.IsError
            ? $"[NPC:{npcId}] move failed: {result.Message}"
            : $"[NPC:{npcId}] {result.Message}");

        if (!string.IsNullOrEmpty(request.transactionId))
            _ = McpAsyncClient.Instance.SendMcpResponseAsync(request.transactionId, result.Message, result.IsError);
    }

    private string MoveToLandmark(MoveArgs args)
    {
        if (_navAgent == null)
            throw new InvalidOperationException($"NPC '{npcId}' 没有 NavMeshAgent。 ");
        if (!_navAgent.isOnNavMesh)
            throw new InvalidOperationException($"NPC '{npcId}' 当前不在 NavMesh 上。");

        Transform landmark = ResolveLandmark(args.targetLandmark);
        if (landmark == null)
            throw new InvalidOperationException($"场景中未配置地标 '{args.targetLandmark}'。");

        if (!NavMesh.SamplePosition(landmark.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            throw new InvalidOperationException($"地标 '{args.targetLandmark}' 附近没有可行走的 NavMesh。");

        if (!_navAgent.SetDestination(hit.position))
            throw new InvalidOperationException($"无法为 NPC '{npcId}' 设置前往 '{args.targetLandmark}' 的路径。");

        _fsmState = NpcState.Operating;
        return $"NPC 已开始前往 {args.targetLandmark}";
    }

    private Transform ResolveLandmark(string landmarkName)
    {
        Transform configured = landmarkName switch
        {
            "warehouse" => warehouseLandmark,
            "gate" => gateLandmark,
            _ => null
        };

        if (configured != null)
            return configured;

        GameObject fallback = GameObject.Find(landmarkName);
        return fallback != null ? fallback.transform : null;
    }

    private void OnChatWindowClosed()
    {
        InteractionEnded?.Invoke();
    }
    private void OnDestroy()
    {
        if (_chatWindow != null)
            _chatWindow.Closed -= OnChatWindowClosed;
        if (CommandDispatcher.Instance != null)
            CommandDispatcher.Instance.UnregisterNpc(npcId);
    }
}