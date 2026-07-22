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

    private readonly ConcurrentQueue<UnityToolCommand> _myPrivateQueue = new ConcurrentQueue<UnityToolCommand>();
    private NavMeshAgent _navAgent;
    private NpcState _fsmState = NpcState.Idle;

    public enum NpcState { Idle, Talking, Operating }

    private void Start()
    {
        CommandDispatcher.Instance.RegisterNpc(npcId, this);
        _navAgent = GetComponent<NavMeshAgent>();

        if (_navAgent == null)
            Debug.LogError($"[NPC:{npcId}] NavMeshAgent is missing.", this);
    }

    public void ReceiveCommand(UnityToolCommand request)
    {
        _myPrivateQueue.Enqueue(request);
    }

    private void Update()
    {
        switch (_fsmState)
        {
            case NpcState.Idle:
                if (_myPrivateQueue.TryDequeue(out UnityToolCommand request))
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

    private void ExecuteBusinessLogic(UnityToolCommand request)
    {
        if (request?.Function?.Name != "game_npc_move")
            return;

        var wrapper = new GameToolWrapper<MoveArgs>(MoveToLandmark);
        ToolExecutionResult result = wrapper.Execute(request.Function.ArgumentsJson);

        Debug.Log(result.IsError
            ? $"[NPC:{npcId}] move failed: {result.Message}"
            : $"[NPC:{npcId}] {result.Message}");

        if (!string.IsNullOrEmpty(request.RequestId))
            _ = AgentHostClient.Instance.SendToolResponseAsync(request.RequestId, result.Message, result.IsError, result.ErrorCode);
    }

    private string MoveToLandmark(MoveArgs args)
    {
        if (_navAgent == null)
            throw new ToolExecutionException("NAV_AGENT_MISSING", $"NPC '{npcId}' 没有 NavMeshAgent。");
        if (!_navAgent.isOnNavMesh)
            throw new ToolExecutionException("NPC_NOT_ON_NAVMESH", $"NPC '{npcId}' 当前不在 NavMesh 上。");

        Transform landmark = ResolveLandmark(args.targetLandmark);
        if (landmark == null)
            throw new ToolExecutionException("LANDMARK_NOT_FOUND", $"场景中未配置地标 '{args.targetLandmark}'。");

        if (!NavMesh.SamplePosition(landmark.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            throw new ToolExecutionException("LANDMARK_NOT_ON_NAVMESH", $"地标 '{args.targetLandmark}' 附近没有可行走的 NavMesh。");

        if (!_navAgent.SetDestination(hit.position))
            throw new ToolExecutionException("PATH_NOT_FOUND", $"无法为 NPC '{npcId}' 设置前往 '{args.targetLandmark}' 的路径。");

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

    private void OnDestroy()
    {
        if (CommandDispatcher.Instance != null)
            CommandDispatcher.Instance.UnregisterNpc(npcId);
    }
}
