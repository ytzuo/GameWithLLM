using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class RuntimeToolDefinition
{
    public string Name;
    public string Description;
    public JObject InputSchema;
}

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


    public List<RuntimeToolDefinition> GetRuntimeTools()
    {
        var list = new List<RuntimeToolDefinition>();
        lock (_lock)
        {
            foreach (INpcTool tool in _tools.Values)
            {
                JObject schema = (JObject)tool.InputSchema.DeepClone();
                JObject properties = schema["properties"] as JObject ?? new JObject();
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
                JArray required = schema["required"] as JArray ?? new JArray();
                if (schema["required"] == null)
                    schema["required"] = required;
                for (int index = required.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(required[index]?.Value<string>(), "entityId", StringComparison.Ordinal))
                        required.RemoveAt(index);
                }
                required.Insert(0, "entityId");
                schema["additionalProperties"] = false;
                list.Add(new RuntimeToolDefinition
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchema = schema
                });
            }
        }
        list.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return list;
    }

    public List<string> GetToolNamesForNpc(NpcEntity npc)
    {
        var names = new List<string>();
        if (npc == null)
            return names;

        var context = new NpcToolContext(npc);
        lock (_lock)
        {
            foreach (INpcTool tool in _tools.Values)
            {
                try
                {
                    if (tool.IsAvailable(context))
                        names.Add(tool.Name);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[ToolsRegistry] 检查 NPC '{npc.npcId}' 的工具 '{tool.Name}' 可用性失败: {ex}");
                }
            }
        }
        names.Sort(StringComparer.Ordinal);
        return names;
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
            if (!tool.IsAvailable(context))
            {
                return ToolExecutionResult.Failure(
                    "TOOL_UNAVAILABLE",
                    $"工具 '{toolName}' 当前不适用于 NPC '{context?.Npc?.npcId}'。");
            }
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
