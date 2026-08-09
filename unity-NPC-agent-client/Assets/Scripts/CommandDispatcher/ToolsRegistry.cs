using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Unity 工具的唯一注册表，同时生成对外 Runtime Manifest Schema。
public class ToolsRegistry : Singleton<ToolsRegistry>
{
    private readonly Dictionary<string, IAgentTool> _tools =
        new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new object();

    public event Action ToolsChanged;

    protected override void Init()
    {
        base.Init();
        AgentToolDiscovery.RegisterAll(this);
    }

    // 注册时立即验证名称和 Schema，避免把无效契约发布给 Gateway。
    public void RegisterTool(IAgentTool tool)
    {
        if (tool == null)
            throw new ArgumentNullException(nameof(tool));
        AgentToolDescriptor descriptor = tool.Descriptor ??
            throw new InvalidOperationException(
                $"工具类型 '{tool.GetType().FullName}' 没有 Descriptor。");
        if (string.IsNullOrWhiteSpace(descriptor.Name))
            throw new InvalidOperationException(
                $"工具类型 '{tool.GetType().FullName}' 的名称不能为空。");
        if (string.IsNullOrWhiteSpace(descriptor.InputSchemaJson))
            throw new InvalidOperationException(
                $"工具 '{descriptor.Name}' 的 inputSchema 不能为空。");
        try
        {
            if (!(JToken.Parse(descriptor.InputSchemaJson) is JObject))
                throw new InvalidOperationException("inputSchema 必须是 JSON 对象。");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"工具 '{descriptor.Name}' 的 inputSchema 无效：{ex.Message}",
                ex);
        }

        lock (_lock)
        {
            if (_tools.TryGetValue(descriptor.Name, out IAgentTool existing))
            {
                if (existing.GetType() == tool.GetType())
                    return;
                throw new InvalidOperationException(
                    $"工具名称重复注册：'{descriptor.Name}' 同时由 " +
                    $"'{existing.GetType().FullName}' 和 '{tool.GetType().FullName}' 声明。");
            }
            _tools.Add(descriptor.Name, tool);
        }
        ToolsChanged?.Invoke();
    }

    // 在业务 Schema 外层注入必需的 entityId；业务工具本身无需声明路由字段。
    public List<AgentToolDescriptor> GetRuntimeTools()
    {
        var list = new List<AgentToolDescriptor>();
        lock (_lock)
        {
            foreach (IAgentTool tool in _tools.Values)
            {
                AgentToolDescriptor descriptor = tool.Descriptor;
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
                    if (string.Equals(
                            required[index]?.Value<string>(),
                            "entityId",
                            StringComparison.Ordinal))
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
        }
        list.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Name, right.Name));
        return list;
    }

    // 按实体实时探测工具能力，用于触发完整 Manifest 更新。
    public List<string> GetAvailableToolNames(IAgentEntity entity)
    {
        var names = new List<string>();
        if (entity == null)
            return names;
        var context = new AgentToolContext(entity, "capability-probe");
        lock (_lock)
        {
            foreach (IAgentTool tool in _tools.Values)
            {
                try
                {
                    if (tool.IsAvailable(context))
                        names.Add(tool.Descriptor.Name);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[ToolsRegistry] 检查实体 '{entity.EntityId}' 的工具 " +
                        $"'{tool.Descriptor.Name}' 可用性失败: {ex}");
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // 执行前重新校验工具注册和实时可用性，并将业务异常转换为 AgentToolResult。
    public async ValueTask<AgentToolResult> ExecuteAsync(
        string toolName,
        AgentToolContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return AgentToolResult.Failure("INVALID_TOOL_NAME", "工具名称不能为空。");

        IAgentTool tool;
        lock (_lock)
        {
            if (!_tools.TryGetValue(toolName, out tool))
            {
                return AgentToolResult.Failure(
                    "UNKNOWN_TOOL",
                    $"未注册工具 '{toolName}'。");
            }
        }

        try
        {
            if (!tool.IsAvailable(context))
            {
                return AgentToolResult.Failure(
                    "TOOL_UNAVAILABLE",
                    $"工具 '{toolName}' 当前不适用于实体 '{context?.Entity?.EntityId}'。");
            }
            return await tool.ExecuteAsync(
                context,
                argumentsJson,
                cancellationToken);
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
