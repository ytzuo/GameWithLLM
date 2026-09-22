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
    private ToolMetadataCatalog _activeCatalog;

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
                    InitializeBuiltinState();
                return _activeSnapshot;
            }
        }
    }

    public ToolMetadataCatalog ActiveCatalog
    {
        get
        {
            _ = ActiveSnapshot;
            return Volatile.Read(ref _activeCatalog);
        }
    }

    protected override void Init()
    {
        base.Init();
        if (_activeSnapshot == null)
            InitializeBuiltinState();
    }

    private void InitializeBuiltinState()
    {
        IReadOnlyList<IAgentTool> builtins = AgentToolDiscovery.DiscoverBuiltinTools();
        ToolSetCandidate baseline = ToolSetCandidateFactory.CreateBuiltinDefault(builtins);
        ToolSetSnapshot snapshot = ToolSetValidator.Prepare(baseline, null);
        Volatile.Write(ref _activeSnapshot, snapshot);
        Volatile.Write(
            ref _activeCatalog,
            ToolMetadataCatalog.CreateCompatibilityCatalog(snapshot, baseline.CatalogVersion));
    }

    // 所有反射发现、Descriptor、Schema、版本和历史校验都在活动锁外完成。
    public PreparedToolSet PrepareToolSet(ToolSetCandidate candidate)
    {
        ToolSetSnapshot current = ActiveSnapshot;
        ToolSetSnapshot prepared = ToolSetValidator.Prepare(candidate, current);
        ToolMetadataCatalog catalog = ToolMetadataCatalog.CreateCompatibilityCatalog(
            prepared,
            candidate.CatalogVersion);
        return new PreparedToolSet(
            prepared,
            catalog,
            current?.Fingerprint,
            ActiveCatalog?.Fingerprint);
    }

    // 发布路径必须把 ToolSet 和 JSON Catalog 一起准备；任一验证失败都不改变活动状态。
    public PreparedToolSet PrepareToolSet(ToolSetCandidate candidate, string toolMetadataJson)
    {
        ToolSetSnapshot current = ActiveSnapshot;
        ToolSetSnapshot prepared = ToolSetValidator.Prepare(candidate, current);
        ToolMetadataCatalog catalog = ToolMetadataCatalog.ParseAndValidate(
            toolMetadataJson,
            prepared,
            candidate.CatalogVersion);
        return new PreparedToolSet(
            prepared,
            catalog,
            current?.Fingerprint,
            ActiveCatalog?.Fingerprint);
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
            ToolMetadataCatalog currentCatalog = _activeCatalog;
            if (current != null && string.Equals(
                    current.Fingerprint,
                    prepared.Snapshot.Fingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(currentCatalog?.Fingerprint, prepared.Catalog.Fingerprint, StringComparison.Ordinal))
                return new ToolSetActivationResult(false, true, current);
            if (!string.Equals(current?.Fingerprint, prepared.BaseFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The prepared ToolSet is stale because another snapshot was activated first.");
            if (!string.Equals(currentCatalog?.Fingerprint, prepared.BaseCatalogFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The prepared ToolSet is stale because another Catalog was activated first.");
            Volatile.Write(ref _activeCatalog, prepared.Catalog);
            Volatile.Write(ref _activeSnapshot, prepared.Snapshot);
            result = prepared.Snapshot;
        }
        ToolsChanged?.Invoke();
        return new ToolSetActivationResult(true, false, result);
    }

    public PreparedToolMetadataCatalog PrepareCatalog(string toolMetadataJson)
    {
        ToolSetSnapshot snapshot = ActiveSnapshot;
        ToolMetadataCatalog catalog = ToolMetadataCatalog.ParseAndValidate(toolMetadataJson, snapshot);
        return new PreparedToolMetadataCatalog(
            catalog,
            snapshot.Fingerprint,
            ActiveCatalog?.Fingerprint);
    }

    // 纯文案更新/回滚不触碰执行路由，只原子交换 Catalog，并发布一次完整 Manifest。
    public bool ActivateCatalog(PreparedToolMetadataCatalog prepared)
    {
        if (prepared == null)
            throw new ArgumentNullException(nameof(prepared));
        lock (_activationLock)
        {
            if (!string.Equals(_activeSnapshot?.Fingerprint, prepared.BaseToolSetFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The prepared Catalog is stale because the ToolSet changed.");
            if (string.Equals(_activeCatalog?.Fingerprint, prepared.Catalog.Fingerprint, StringComparison.Ordinal))
                return false;
            if (!string.Equals(_activeCatalog?.Fingerprint, prepared.BaseCatalogFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("The prepared Catalog is stale because another Catalog was activated first.");
            Volatile.Write(ref _activeCatalog, prepared.Catalog);
        }
        ToolsChanged?.Invoke();
        return true;
    }

    // 在业务 Schema 外层注入必需的 entityId；业务工具本身无需声明路由字段。
    public List<AgentToolDescriptor> GetRuntimeTools()
    {
        _ = ActiveSnapshot;
        ToolSetSnapshot snapshot;
        ToolMetadataCatalog catalog;
        lock (_activationLock)
        {
            snapshot = _activeSnapshot;
            catalog = _activeCatalog;
        }
        var list = new List<AgentToolDescriptor>(snapshot?.Tools.Count ?? 0);
        if (snapshot == null)
            return list;
        foreach (KeyValuePair<string, IAgentTool> entry in snapshot.Tools)
        {
            AgentToolDescriptor descriptor = catalog.Apply(snapshot.Descriptors[entry.Key]);
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
                return AgentToolResult.Failure(
                    "TOOL_RETIRED",
                    ClientTextCatalogs.Message("tool.error.retired", "工具 '{0}' 已停用。", toolName));
            return AgentToolResult.Failure(
                "UNKNOWN_TOOL",
                ClientTextCatalogs.Message("tool.error.unknown", "未注册工具 '{0}'。", toolName));
        }

        try
        {
            if (!tool.IsAvailable(context))
            {
                return AgentToolResult.Failure(
                    "TOOL_UNAVAILABLE",
                    ClientTextCatalogs.Message(
                        "tool.error.unavailable",
                        "工具 '{0}' 当前不适用于实体 '{1}'。",
                        toolName,
                        context?.Entity?.EntityId));
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
                ClientTextCatalogs.Message(
                    "tool.error.execution_failed",
                    "工具 '{0}' 执行失败：{1}",
                    toolName,
                    ex.Message));
        }
    }
}
