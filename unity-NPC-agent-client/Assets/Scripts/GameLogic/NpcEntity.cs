using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class NpcEntity : MonoBehaviour, IGameObjectAgentEntity
{
    public string npcId;

    [Header("Movement")]
    [SerializeField, Min(0.5f)] private float moveStoppingDistance = 1.5f;
    [SerializeField, Min(0.1f)] private float dynamicTargetRepathInterval = 0.5f;
    [SerializeField, Min(0.1f)] private float dynamicTargetMoveThreshold = 0.5f;
    [SerializeField, Min(1f)] private float maximumMoveDuration = 45f;

    [Header("Inventory tools")]
    [SerializeField, Min(0f)] private float inventoryInteractionRange = 3f;

    private readonly ConcurrentQueue<AgentToolCommand> _myPrivateQueue = new ConcurrentQueue<AgentToolCommand>();
    private NavMeshAgent _navAgent;
    private NpcState _fsmState = NpcState.Idle;
    private AgentToolCommand _activeCommand;
    private NpcTargetRecord _activeMoveTarget;
    private int _movementStartedFrame;
    private float _movementStartedAt;
    private float _nextRepathTime;
    private float _nextProgressTime;
    private float _activeApproachDistance;
    private Vector3 _lastMoveDestination;
    private List<string> _lastAvailableToolNames = new List<string>();
    private float _nextCapabilityCheckAt;

    public enum NpcState { Idle, Talking, Operating }

    public string EntityId => npcId;
    public bool IsOnline => isActiveAndEnabled;
    public GameObject GameObject => gameObject;

    internal float InventoryInteractionRange => inventoryInteractionRange;
    internal bool IsOnNavMesh => _navAgent != null && _navAgent.isOnNavMesh;

    internal JToken CreateRuntimeStateData()
    {
        Vector3 position = transform.position;
        bool isOnNavMesh = _navAgent != null && _navAgent.isOnNavMesh;
        bool isMoving = _fsmState == NpcState.Operating && _activeMoveTarget != null;
        JToken remainingDistance = JValue.CreateNull();
        if (isMoving && isOnNavMesh && !_navAgent.pathPending &&
            !float.IsInfinity(_navAgent.remainingDistance) && !float.IsNaN(_navAgent.remainingDistance))
        {
            remainingDistance = new JValue(System.Math.Round(_navAgent.remainingDistance, 2));
        }

        JToken targetPosition = JValue.CreateNull();
        if (isMoving && _activeMoveTarget.GameObject != null)
        {
            Vector3 target = _activeMoveTarget.GameObject.transform.position;
            targetPosition = JToken.FromObject(new
            {
                x = System.Math.Round(target.x, 2),
                y = System.Math.Round(target.y, 2),
                z = System.Math.Round(target.z, 2)
            });
        }

        return JToken.FromObject(new
        {
            npcId,
            targetId = string.IsNullOrWhiteSpace(npcId) ? null : $"npc:{npcId}",
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
                targetId = isMoving ? _activeMoveTarget.TargetId : null,
                targetPosition,
                isTrackingDynamicTarget = isMoving && _activeMoveTarget.IsDynamic,
                pathPending = isMoving && _navAgent != null && _navAgent.pathPending,
                pathStatus = GetMovementPathStatus(isMoving, isOnNavMesh),
                remainingDistance,
                approachDistance = isMoving
                    ? System.Math.Round(_activeApproachDistance, 2)
                    : System.Math.Round(moveStoppingDistance, 2),
                elapsedSeconds = isMoving ? System.Math.Round(Time.time - _movementStartedAt, 2) : 0
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
        WorldModelInitial.Attach(gameObject, npcId);
        _navAgent = GetComponent<NavMeshAgent>();
        if (_navAgent == null)
            Debug.LogError($"[NPC:{npcId}] NavMeshAgent is missing.", this);

        _lastAvailableToolNames = ToolsRegistry.Instance.GetToolNamesForNpc(this);
        CommandDispatcher.Instance.RegisterNpc(npcId, this);
    }

    public void ReceiveCommand(AgentToolCommand request)
    {
        _myPrivateQueue.Enqueue(request);
    }

    private void Update()
    {
        RefreshCapabilitiesIfNeeded();

        switch (_fsmState)
        {
            case NpcState.Idle:
                if (_myPrivateQueue.TryDequeue(out AgentToolCommand request))
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

    private void RefreshCapabilitiesIfNeeded()
    {
        if (Time.unscaledTime < _nextCapabilityCheckAt)
            return;
        _nextCapabilityCheckAt = Time.unscaledTime + 0.5f;

        List<string> available = ToolsRegistry.Instance.GetToolNamesForNpc(this);
        if (available.Count == _lastAvailableToolNames.Count)
        {
            bool unchanged = true;
            for (int i = 0; i < available.Count; i++)
            {
                if (!string.Equals(available[i], _lastAvailableToolNames[i], StringComparison.Ordinal))
                {
                    unchanged = false;
                    break;
                }
            }
            if (unchanged)
                return;
        }

        _lastAvailableToolNames = available;
        CommandDispatcher.Instance.NotifyNpcCapabilitiesChanged(npcId, this);
    }

    private void ExecuteBusinessLogic(AgentToolCommand request)
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

        if (result.IsPending)
        {
            _activeCommand = request;
            return;
        }

        if (!string.IsNullOrEmpty(request?.RequestId))
        {
            request.TryComplete(result);
            CommandDispatcher.Instance.CompleteRequest(request.RequestId);
        }
    }

    internal void MoveToTarget(MoveArgs args)
    {
        if (_navAgent == null)
            throw new ToolExecutionException("NAV_AGENT_MISSING", $"NPC '{npcId}' 没有 NavMeshAgent。");
        if (!_navAgent.isOnNavMesh)
            throw new ToolExecutionException("NPC_NOT_ON_NAVMESH", $"NPC '{npcId}' 当前不在 NavMesh 上。");

        _activeMoveTarget = NpcTargetSupport.ResolveUniqueTarget(args.targetId);
        if (_activeMoveTarget.GameObject == gameObject)
        {
            _activeMoveTarget = null;
            throw new ToolExecutionException("TARGET_IS_SELF", "NPC 不能移动到自己身边。");
        }

        _activeApproachDistance = args.approachDistance > 0f ? args.approachDistance : moveStoppingDistance;
        _navAgent.stoppingDistance = _activeApproachDistance;
        _navAgent.autoBraking = true;
        if (!TrySetActiveDestination(out string errorCode, out string errorMessage))
        {
            _activeMoveTarget = null;
            throw new ToolExecutionException(errorCode, errorMessage);
        }

        _movementStartedFrame = Time.frameCount;
        _movementStartedAt = Time.time;
        _nextProgressTime = Time.time;
        _fsmState = NpcState.Operating;
    }

    private bool TrySetActiveDestination(out string errorCode, out string errorMessage)
    {
        if (_activeMoveTarget?.GameObject == null)
        {
            errorCode = "TARGET_UNAVAILABLE";
            errorMessage = "移动目标已销毁或离线。";
            return false;
        }
        if (!NavMesh.SamplePosition(_activeMoveTarget.GameObject.transform.position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
        {
            errorCode = "TARGET_NOT_ON_NAVMESH";
            errorMessage = $"目标 '{_activeMoveTarget.TargetId}' 附近没有可行走的 NavMesh。";
            return false;
        }
        if (!_navAgent.SetDestination(hit.position))
        {
            errorCode = "PATH_NOT_FOUND";
            errorMessage = $"无法为 NPC '{npcId}' 设置前往 '{_activeMoveTarget.TargetId}' 的路径。";
            return false;
        }

        _lastMoveDestination = hit.position;
        _nextRepathTime = Time.time + dynamicTargetRepathInterval;
        errorCode = null;
        errorMessage = null;
        return true;
    }

    private void UpdateActiveMovement()
    {
        if (_activeCommand == null || _activeMoveTarget == null)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("MOVE_STATE_INVALID", $"NPC '{npcId}' 的移动状态不完整。"));
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
        if (_activeMoveTarget.GameObject == null)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("TARGET_UNAVAILABLE", "移动目标已销毁或离线。"));
            return;
        }
        if (Time.time >= _nextProgressTime)
        {
            _nextProgressTime = Time.time + 0.5f;
            double progress = Math.Min(
                0.95,
                Math.Max(0.0, (Time.time - _movementStartedAt) / maximumMoveDuration));
            _activeCommand.Progress?.Invoke(progress, "moving");
        }
        if (Time.time - _movementStartedAt > maximumMoveDuration)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("MOVE_TIMEOUT", $"NPC 未能在 {maximumMoveDuration:0.#} 秒内到达 '{_activeMoveTarget.TargetId}'。"));
            return;
        }

        if (_activeMoveTarget.IsDynamic && Time.time >= _nextRepathTime)
        {
            Vector3 currentTargetPosition = _activeMoveTarget.GameObject.transform.position;
            _nextRepathTime = Time.time + dynamicTargetRepathInterval;
            if (Vector3.Distance(currentTargetPosition, _lastMoveDestination) >= dynamicTargetMoveThreshold &&
                !TrySetActiveDestination(out string errorCode, out string errorMessage))
            {
                FinishActiveMovement(ToolExecutionResult.Failure(errorCode, errorMessage));
                return;
            }
        }

        if (Time.frameCount <= _movementStartedFrame || _navAgent.pathPending)
            return;
        if (_navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("PATH_INVALID", $"NPC '{npcId}' 无法到达目标 '{_activeMoveTarget.TargetId}'。"));
            return;
        }
        if (_navAgent.pathStatus == NavMeshPathStatus.PathPartial)
        {
            FinishActiveMovement(ToolExecutionResult.Failure("PATH_PARTIAL", $"NPC '{npcId}' 只能部分接近目标 '{_activeMoveTarget.TargetId}'。"));
            return;
        }
        if (_navAgent.hasPath && _navAgent.remainingDistance > _navAgent.stoppingDistance)
            return;

        string arrivedTargetId = _activeMoveTarget.TargetId;
        float elapsed = Time.time - _movementStartedAt;
        FinishActiveMovement(ToolExecutionResult.Success(
            JToken.FromObject(new
            {
                targetId = arrivedTargetId,
                approachDistance = System.Math.Round(_activeApproachDistance, 2),
                elapsedSeconds = System.Math.Round(elapsed, 2)
            }),
            $"NPC 已到达 {arrivedTargetId} 附近。"));
    }
    private void FinishActiveMovement(ToolExecutionResult result)
    {
        AgentToolCommand command = _activeCommand;
        if (!string.IsNullOrEmpty(command?.RequestId))
        {
            command.TryComplete(result);
        }
        ClearActiveMovement();
    }

    private void ClearActiveMovement()
    {
        string requestId = _activeCommand?.RequestId;
        _activeCommand = null;
        _activeMoveTarget = null;
        _movementStartedFrame = 0;
        _movementStartedAt = 0f;
        _nextRepathTime = 0f;
        _nextProgressTime = 0f;
        _activeApproachDistance = 0f;
        _lastMoveDestination = default;
        _fsmState = NpcState.Idle;
        if (CommandDispatcher.Instance != null)
            CommandDispatcher.Instance.CompleteRequest(requestId);
    }
    /// <summary>存档加载专用：丢弃旧世界命令并把 NPC 放回保存位置。</summary>
    internal void RestoreWorldTransform(Vector3 position, Quaternion rotation)
    {
        while (_myPrivateQueue.TryDequeue(out AgentToolCommand queued))
        {
            if (CommandDispatcher.Instance != null)
                CommandDispatcher.Instance.CompleteRequest(queued?.RequestId);
        }
        if (_navAgent != null && _navAgent.isOnNavMesh)
            _navAgent.ResetPath();
        ClearActiveMovement();

        transform.rotation = rotation;
        if (_navAgent != null && _navAgent.enabled && _navAgent.isOnNavMesh &&
            NavMesh.SamplePosition(position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
        {
            _navAgent.Warp(hit.position);
        }
        else
        {
            transform.position = position;
        }
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
