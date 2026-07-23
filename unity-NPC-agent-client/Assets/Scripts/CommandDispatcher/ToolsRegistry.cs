using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class ToolsRegistry : Singleton<ToolsRegistry>
{
    private readonly Dictionary<string, INpcTool> _tools =
        new Dictionary<string, INpcTool>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new object();

    public event Action ToolsChanged;

    protected override void Init()
    {
        base.Init();
        NpcToolDiscovery.RegisterAll(this);
    }

    public void RegisterTool(INpcTool tool)
    {
        if (tool == null)
            throw new ArgumentNullException(nameof(tool));
        if (string.IsNullOrWhiteSpace(tool.Name))
            throw new InvalidOperationException($"工具类型 '{tool.GetType().FullName}' 的名称不能为空。");
        if (tool.InputSchema == null)
            throw new InvalidOperationException($"工具 '{tool.Name}' 的 inputSchema 不能为空。");

        lock (_lock)
        {
            if (_tools.TryGetValue(tool.Name, out INpcTool existing))
            {
                if (existing.GetType() == tool.GetType())
                    return;

                throw new InvalidOperationException(
                    $"工具名称重复注册：'{tool.Name}' 同时由 " +
                    $"'{existing.GetType().FullName}' 和 '{tool.GetType().FullName}' 声明。");
            }

            _tools.Add(tool.Name, tool);
        }
        ToolsChanged?.Invoke();
    }


    public List<UnityGatewayToolDefinition> GetToolsForGateway()
    {
        var list = new List<UnityGatewayToolDefinition>();
        lock (_lock)
        {
            foreach (INpcTool tool in _tools.Values)
            {
                list.Add(new UnityGatewayToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = (JObject)tool.InputSchema.DeepClone()
                });
            }
        }
        list.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return list;
    }

    public ToolExecutionResult Execute(
        string toolName,
        NpcToolContext context,
        string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return ToolExecutionResult.Failure("INVALID_TOOL_NAME", "工具名称不能为空。");

        INpcTool tool;
        lock (_lock)
        {
            if (!_tools.TryGetValue(toolName, out tool))
            {
                return ToolExecutionResult.Failure(
                    "UNKNOWN_TOOL",
                    $"未注册工具 '{toolName}'。");
            }
        }

        try
        {
            return tool.Execute(context, argumentsJson);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ToolsRegistry] 工具 '{toolName}' 发生未处理异常: {ex}");
            return ToolExecutionResult.Failure(
                "TOOL_EXECUTION_FAILED",
                $"工具 '{toolName}' 执行失败：{ex.Message}");
        }
    }
}
