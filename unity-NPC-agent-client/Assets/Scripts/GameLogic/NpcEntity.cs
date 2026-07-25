using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class NpcEntity : MonoBehaviour
{
    public string npcId;

    [Header("Movement")]
    [SerializeField, Min(0.5f)] private float moveStoppingDistance = 1.5f;

    [Header("Inventory tools")]
    [SerializeField, Min(0f)] private float inventoryInteractionRange = 3f;

    private readonly ConcurrentQueue<UnityToolCommand> _myPrivateQueue = new ConcurrentQueue<UnityToolCommand>();
    private NavMeshAgent _navAgent;
    private NpcState _fsmState = NpcState.Idle;
    private UnityToolCommand _activeCommand;
    private string _activeMoveTarget;
    private int _movementStartedFrame;

    public enum NpcState { Idle, Talking, Operating }

    internal float InventoryInteractionRange => inventoryInteractionRange;
    internal bool IsOnNavMesh => _navAgent != null && _navAgent.isOnNavMesh;

    internal JToken CreateRuntimeStateData()
    {
        Vector3 position = transform.position;
        bool isOnNavMesh = _navAgent != null && _navAgent.isOnNavMesh;
        bool isMoving = _fsmState == NpcState.Operating && !string.IsNullOrEmpty(_activeMoveTarget);
        JToken remainingDistance = JValue.CreateNull();
        if (isMoving && isOnNavMesh && !_navAgent.pathPending &&
            !float.IsInfinity(_navAgent.remainingDistance) &&
            !float.IsNaN(_navAgent.remainingDistance))
        {
            remainingDistance = new JValue(System.Math.Round(_navAgent.remainingDistance, 2));
        }

        return JToken.FromObject(new
        {
            npcId,
            state = _fsmState.ToString().ToLowerInvariant(),
            position = new
            {
                x = System.Math.Round(position.x, 2),
                y = System.Math.Round(position.y, 2),
                z = System.Math.Round(position.z, 2)
            },
            isOnNavMesh,
            movement = new
            {
                isMoving,
                targetLandmark = isMoving ? _activeMoveTarget : null,
                pathPending = isMoving && _navAgent != null && _navAgent.pathPending,
                pathStatus = GetMovementPathStatus(isMoving, isOnNavMesh),
                remainingDistance,
                stoppingDistance = System.Math.Round(moveStoppingDistance, 2)
            }
        });
    }

    private string GetMovementPathStatus(bool isMoving, bool isOnNavMesh)
    {
        if (!isMoving)
            return "idle";
        if (!isOnNavMesh)
            return "unavailable";
        if (_navAgent.pathPending)
            return "pending";

        switch (_navAgent.pathStatus)
        {
            case NavMeshPathStatus.PathComplete:
                return "complete";
            case NavMeshPathStatus.PathPartial:
                return "partial";
            case NavMeshPathStatus.PathInvalid:
                return "invalid";
            default:
                return "unknown";
        }
    }

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
                {
                    if (CommandDispatcher.Instance.IsCancellationRequested(request.RequestId))
                        CommandDispatcher.Instance.CompleteRequest(request.RequestId);
                    else
                        ExecuteBusinessLogic(request);
                }
                break;

            case NpcState.Operating:
                UpdateActiveMovement();
                break;
        }
    }

    private void ExecuteBusinessLogic(UnityToolCommand request)
    {
        ToolExecutionResult result;
        if (request?.Function == null)
        {
            result = ToolExecutionResult.Failure("INVALID_COMMAND", "工具命令或函数信息缺失。");
        }
        else
        {
            var context = new NpcToolContext(this);
            result = ToolsRegistry.Instance.Execute(
                request.Function.Name,
                context,
                request.Function.ArgumentsJson);
        }

        var resultTrace = new JObject
        {
            ["event"] = "unity_tool_executed",
            ["requestId"] = request?.RequestId,
            ["npcId"] = npcId,
            ["tool"] = request?.Function?.Name,
            ["ok"] = !result.IsError,
            ["pending"] = result.IsPending
        };
        if (!string.IsNullOrEmpty(result.ErrorCode))
            resultTrace["errorCode"] = result.ErrorCode;
        if (!string.IsNullOrEmpty(result.Message))
            resultTrace["message"] = result.Message;
        if (result.Data != null)
            resultTrace["data"] = result.Data.DeepClone();
        Debug.Log($"[Unity Tool Trace] {resultTrace.ToString(Formatting.None)}", this);

        if (result.IsPending)
        {
            _activeCommand = request;
            return;
        }

        if (!string.IsNullOrEmpty(request?.RequestId))
        {
            _ = AgentHostClient.Instance.SendToolResponseAsync(
                request.RequestId,
                result.Message,
                result.IsError,
                result.ErrorCode,
                result.Data);
            CommandDispatcher.Instance.CompleteRequest(request.RequestId);
        }
    }

    internal void MoveToLandmark(MoveArgs args)
    {
        if (_navAgent == null)
            throw new ToolExecutionException("NAV_AGENT_MISSING", $"NPC '{npcId}' 没有 NavMeshAgent。");
        if (!_navAgent.isOnNavMesh)
            throw new ToolExecutionException("NPC_NOT_ON_NAVMESH", $"NPC '{npcId}' 当前不在 NavMesh 上。");

        Transform landmark = NpcTargetSupport.ResolveUniqueTarget(args.targetLandmark).transform;

        if (!NavMesh.SamplePosition(landmark.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            throw new ToolExecutionException("LANDMARK_NOT_ON_NAVMESH", $"地标 '{args.targetLandmark}' 附近没有可行走的 NavMesh。");

        _navAgent.stoppingDistance = moveStoppingDistance;
        _navAgent.autoBraking = true;

        if (!_navAgent.SetDestination(hit.position))
            throw new ToolExecutionException("PATH_NOT_FOUND", $"无法为 NPC '{npcId}' 设置前往 '{args.targetLandmark}' 的路径。");

        _activeMoveTarget = args.targetLandmark;
        _movementStartedFrame = Time.frameCount;
        _fsmState = NpcState.Operating;
    }

    private void UpdateActiveMovement()
    {
        if (_activeCommand == null)
        {
            FinishActiveMovement(ToolExecutionResult.Failure(
                "MOVE_STATE_INVALID",
                $"NPC '{npcId}' 的移动状态缺少活动命令。"));
            return;
        }

        if (CommandDispatcher.Instance.IsCancellationRequested(_activeCommand.RequestId))
        {
            if (_navAgent != null && _navAgent.isOnNavMesh)
                _navAgent.ResetPath();
            ClearActiveMovement();
            return;
        }

        if (_navAgent == null)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("NAV_AGENT_MISSING", $"NPC '{npcId}' 没有 NavMeshAgent。"));
            return;
        }

        if (!_navAgent.isOnNavMesh)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("NPC_NOT_ON_NAVMESH", $"NPC '{npcId}' 在移动过程中离开了 NavMesh。"));
            return;
        }

        if (Time.frameCount <= _movementStartedFrame || _navAgent.pathPending)
            return;

        if (_navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("PATH_INVALID", $"NPC '{npcId}' 无法到达地标 '{_activeMoveTarget}'。"));
            return;
        }

        if (_navAgent.pathStatus == NavMeshPathStatus.PathPartial)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("PATH_PARTIAL", $"NPC '{npcId}' 只能部分接近地标 '{_activeMoveTarget}'。"));
            return;
        }

        if (_navAgent.hasPath && _navAgent.remainingDistance > _navAgent.stoppingDistance)
            return;

        FinishActiveMovement(ToolExecutionResult.Success(
            $"NPC 已到达 {_activeMoveTarget} 附近，距离目标约 {_navAgent.stoppingDistance:0.##} 米。"));
    }

    private void FinishActiveMovement(ToolExecutionResult result)
    {
        UnityToolCommand command = _activeCommand;
        if (!string.IsNullOrEmpty(command?.RequestId))
        {
            _ = AgentHostClient.Instance.SendToolResponseAsync(
                command.RequestId,
                result.Message,
                result.IsError,
                result.ErrorCode,
                result.Data);
        }
        ClearActiveMovement();
    }

    private void ClearActiveMovement()
    {
        string requestId = _activeCommand?.RequestId;
        _activeCommand = null;
        _activeMoveTarget = null;
        _movementStartedFrame = 0;
        _fsmState = NpcState.Idle;
        if (CommandDispatcher.Instance != null)
            CommandDispatcher.Instance.CompleteRequest(requestId);
    }

    private void OnDestroy()
    {
        if (_activeCommand != null)
        {
            FinishActiveMovement(ToolExecutionResult.Failure(
                "NPC_DESTROYED",
                $"NPC '{npcId}' 在移动完成前被销毁。"));
        }
        if (CommandDispatcher.Instance != null)
            CommandDispatcher.Instance.UnregisterNpc(npcId);
    }
}
