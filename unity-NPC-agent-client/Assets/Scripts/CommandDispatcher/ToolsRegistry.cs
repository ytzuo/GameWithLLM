using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Unity 工具的唯一注册表。活动状态始终是一个不可变 ToolSet 快照。
public class ToolsRegistry : Singleton<ToolsRegistry>
{
    private readonly object _activationLock = new object();
    private ToolSetSnapshot _activeSnapshot;

    public event Action ToolsChanged;

    public ToolSetSnapshot ActiveSnapshot
    {
        get
        {
            ToolSetSnapshot snapshot = Volatile.Read(ref _activeSnapshot);
            if (snapshot != null)
                return snapshot;
            lock (_activationLock)
            {
                if (_activeSnapshot == null)
                    _activeSnapshot = CreateBuiltinSnapshot();
                return _activeSnapshot;
            }
        }
    }

    protected override void Init()
    {
        base.Init();
        if (_activeSnapshot == null)
            _activeSnapshot = CreateBuiltinSnapshot();
    }

    private static ToolSetSnapshot CreateBuiltinSnapshot()
    {
        IReadOnlyList<IAgentTool> builtins = AgentToolDiscovery.DiscoverBuiltinTools();
        ToolSetCandidate baseline = ToolSetCandidateFactory.CreateBuiltinDefault(builtins);
        return ToolSetValidator.Prepare(baseline, null);
    }

    // 所有反射发现、Descriptor、Schema、版本和历史校验都在活动锁外完成。
    public PreparedToolSet PrepareToolSet(ToolSetCandidate candidate)
    {
        ToolSetSnapshot current = ActiveSnapshot;
        ToolSetSnapshot prepared = ToolSetValidator.Prepare(candidate, current);
        return new PreparedToolSet(prepared, current?.Fingerprint);
    }

    // 只有完整候选可进入这里；锁内仅验证基线并原子交换一次快照引用。
    public ToolSetActivationResult ActivateToolSet(PreparedToolSet prepared)
    {
        if (prepared == null)
            throw new ArgumentNullException(nameof(prepared));
        ToolSetSnapshot result;
        lock (_activationLock)
        {
            ToolSetSnapshot current = _activeSnapshot;
            if (current != null && string.Equals(
                    current.Fingerprint,
                    prepared.Snapshot.Fingerprint,
                    StringComparison.Ordinal))
                return new ToolSetActivationResult(false, true, current);
            if (!string.Equals(current?.Fingerprint, prepared.BaseFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The prepared ToolSet is stale because another snapshot was activated first.");
            Volatile.Write(ref _activeSnapshot, prepared.Snapshot);
            result = prepared.Snapshot;
        }
        ToolsChanged?.Invoke();
        return new ToolSetActivationResult(true, false, result);
    }

    // 在业务 Schema 外层注入必需的 entityId；业务工具本身无需声明路由字段。
    public List<AgentToolDescriptor> GetRuntimeTools()
    {
        ToolSetSnapshot snapshot = ActiveSnapshot;
        var list = new List<AgentToolDescriptor>(snapshot?.Tools.Count ?? 0);
        if (snapshot == null)
            return list;
        foreach (KeyValuePair<string, IAgentTool> entry in snapshot.Tools)
        {
            AgentToolDescriptor descriptor = snapshot.Descriptors[entry.Key];
            var schema = JObject.Parse(descriptor.InputSchemaJson);
            var properties = schema["properties"] as JObject ?? new JObject();
            schema["type"] = "object";
            schema["properties"] = properties;
            properties.Remove("entityId");
            properties.AddFirst(new JProperty(
                "entityId",
                new JObject
                {
                    ["type"] = "string",
                    ["description"] = "执行该行为的游戏实体 ID"
                }));
            var required = schema["required"] as JArray ?? new JArray();
            schema["required"] = required;
            for (int index = required.Count - 1; index >= 0; index--)
            {
                if (string.Equals(required[index]?.Value<string>(), "entityId", StringComparison.Ordinal))
                    required.RemoveAt(index);
            }
            required.Insert(0, "entityId");
            schema["additionalProperties"] = false;
            list.Add(new AgentToolDescriptor(
                descriptor.Name,
                descriptor.Description,
                schema.ToString(Formatting.None),
                descriptor.Interruptible,
                descriptor.SuggestedTimeout));
        }
        list.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return list;
    }

    public List<string> GetAvailableToolNames(IAgentEntity entity)
    {
        var names = new List<string>();
        if (entity == null)
            return names;
        ToolSetSnapshot snapshot = ActiveSnapshot;
        if (snapshot == null)
            return names;
        var context = new AgentToolContext(entity, "capability-probe");
        foreach (KeyValuePair<string, IAgentTool> entry in snapshot.Tools)
        {
            IAgentTool tool = entry.Value;
            try
            {
                if (tool.IsAvailable(context))
                    names.Add(entry.Key);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[ToolsRegistry] 检查实体 '{entity.EntityId}' 的工具 " +
                    $"'{entry.Key}' 可用性失败: {ex}");
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // 开始执行时从一个快照捕获实例。之后的快照切换不影响该次调用。
    public async ValueTask<AgentToolResult> ExecuteAsync(
        string toolName,
        AgentToolContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return AgentToolResult.Failure("INVALID_TOOL_NAME", "工具名称不能为空。");

        ToolSetSnapshot snapshot = ActiveSnapshot;
        if (snapshot == null || !snapshot.Tools.TryGetValue(toolName, out IAgentTool tool))
        {
            if (snapshot?.IsRetired(toolName) == true)
                return AgentToolResult.Failure("TOOL_RETIRED", $"工具 '{toolName}' 已停用。");
            return AgentToolResult.Failure("UNKNOWN_TOOL", $"未注册工具 '{toolName}'。");
        }

        try
        {
            if (!tool.IsAvailable(context))
            {
                return AgentToolResult.Failure(
                    "TOOL_UNAVAILABLE",
                    $"工具 '{toolName}' 当前不适用于实体 '{context?.Entity?.EntityId}'。");
            }
            return await tool.ExecuteAsync(context, argumentsJson, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ToolsRegistry] 工具 '{toolName}' 发生未处理异常: {ex}");
            return AgentToolResult.Failure(
                "TOOL_EXECUTION_FAILED",
                $"工具 '{toolName}' 执行失败：{ex.Message}");
        }
    }
}
