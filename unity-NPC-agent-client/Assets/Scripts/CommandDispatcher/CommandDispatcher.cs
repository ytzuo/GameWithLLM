using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using UnityEngine;

public class CommandDispatcher : Singleton<CommandDispatcher>
{
    private sealed class PendingInvocation
    {
        public RuntimeCommand Command;
        public Action<double, string> Progress;
        public CancellationToken CancellationToken;
        public TaskCompletionSource<AgentToolResult> Completion;
    }

    private readonly Dictionary<string, IAgentEntity> _entities =
        new Dictionary<string, IAgentEntity>(StringComparer.Ordinal);
    private readonly object _entityLock = new object();
    private readonly ConcurrentQueue<PendingInvocation> _incoming =
        new ConcurrentQueue<PendingInvocation>();
    private readonly ConcurrentQueue<string> _completedEntities =
        new ConcurrentQueue<string>();
    private readonly Dictionary<string, Queue<PendingInvocation>> _waitingByEntity =
        new Dictionary<string, Queue<PendingInvocation>>(StringComparer.Ordinal);
    private readonly HashSet<string> _activeEntities =
        new HashSet<string>(StringComparer.Ordinal);

    public event Action<string, bool> EntityChanged;
    public event Action<string> EntityCapabilitiesChanged;

    public void RegisterEntity(IAgentEntity entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.EntityId))
            return;
        lock (_entityLock)
        {
            if (_entities.TryGetValue(entity.EntityId, out IAgentEntity existing) &&
                !ReferenceEquals(existing, entity))
            {
                Debug.LogError(
                    $"[Agent Runtime] 实体 ID 重复：'{entity.EntityId}' 已被注册。");
                return;
            }
            _entities[entity.EntityId] = entity;
        }
        EntityChanged?.Invoke(entity.EntityId, true);
    }

    public void UnregisterEntity(string entityId, IAgentEntity entity = null)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            return;
        lock (_entityLock)
        {
            if (!_entities.TryGetValue(entityId, out IAgentEntity registered) ||
                (entity != null && !ReferenceEquals(registered, entity)))
                return;
            _entities.Remove(entityId);
        }
        EntityChanged?.Invoke(entityId, false);
    }

    public List<string> GetRegisteredEntityIds()
    {
        lock (_entityLock)
        {
            var ids = new List<string>();
            foreach (KeyValuePair<string, IAgentEntity> pair in _entities)
            {
                if (pair.Value?.IsOnline == true)
                    ids.Add(pair.Key);
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
    }

    public void NotifyEntityCapabilitiesChanged(IAgentEntity entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.EntityId))
            return;
        lock (_entityLock)
        {
            if (!_entities.TryGetValue(entity.EntityId, out IAgentEntity registered) ||
                !ReferenceEquals(registered, entity))
                return;
        }
        EntityCapabilitiesChanged?.Invoke(entity.EntityId);
    }

    public Task<AgentToolResult> ExecuteAsync(
        RuntimeCommand command,
        Action<double, string> progress,
        CancellationToken cancellationToken)
    {
        if (command == null)
        {
            return Task.FromResult(
                AgentToolResult.Failure("INVALID_COMMAND", "RuntimeCommand 不能为空。"));
        }

        var completion = new TaskCompletionSource<AgentToolResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken effectiveToken = command.CancellationToken.CanBeCanceled
            ? command.CancellationToken
            : cancellationToken;
        _incoming.Enqueue(new PendingInvocation
        {
            Command = command,
            Progress = progress,
            CancellationToken = effectiveToken,
            Completion = completion
        });
        return completion.Task;
    }

    private void Update()
    {
        while (_completedEntities.TryDequeue(out string completedEntityId))
            _activeEntities.Remove(completedEntityId);

        while (_incoming.TryDequeue(out PendingInvocation invocation))
        {
            string entityId = invocation.Command.EntityId;
            if (!_waitingByEntity.TryGetValue(
                    entityId,
                    out Queue<PendingInvocation> queue))
            {
                queue = new Queue<PendingInvocation>();
                _waitingByEntity.Add(entityId, queue);
            }
            queue.Enqueue(invocation);
        }

        foreach (KeyValuePair<string, Queue<PendingInvocation>> pair in _waitingByEntity)
        {
            if (_activeEntities.Contains(pair.Key))
                continue;

            PendingInvocation invocation = null;
            while (pair.Value.Count > 0)
            {
                PendingInvocation candidate = pair.Value.Dequeue();
                if (candidate.CancellationToken.IsCancellationRequested)
                {
                    candidate.Completion.TrySetCanceled();
                    continue;
                }
                invocation = candidate;
                break;
            }
            if (invocation == null)
                continue;

            IAgentEntity entity;
            lock (_entityLock)
                _entities.TryGetValue(pair.Key, out entity);
            if (entity == null || !entity.IsOnline)
            {
                invocation.Completion.TrySetResult(
                    AgentToolResult.Failure(
                        "ENTITY_NOT_FOUND",
                        $"实体 '{pair.Key}' 未注册或已离线。"));
                continue;
            }

            _activeEntities.Add(pair.Key);
            ExecuteOnMainThreadAsync(invocation, entity);
        }
    }

    private async void ExecuteOnMainThreadAsync(
        PendingInvocation invocation,
        IAgentEntity entity)
    {
        try
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            var context = new AgentToolContext(
                entity,
                invocation.Command.InvocationId,
                invocation.Progress);
            AgentToolResult result = await ToolsRegistry.Instance.ExecuteAsync(
                invocation.Command.ToolName,
                context,
                invocation.Command.ArgumentsJson,
                invocation.CancellationToken);
            invocation.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            invocation.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[Agent Runtime] 工具调用 '{invocation.Command.InvocationId}' " +
                $"发生未处理异常: {ex}");
            invocation.Completion.TrySetResult(
                AgentToolResult.Failure(
                    "TOOL_EXECUTION_FAILED",
                    $"工具执行失败：{ex.Message}"));
        }
        finally
        {
            _completedEntities.Enqueue(entity.EntityId);
        }
    }
}
