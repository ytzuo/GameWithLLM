using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 全局工具注册与发现中心（Tools Discovery）
/// 管理运行时可用的 MCP 工具描述（包括 JSON Schema）和可选的本地执行映射
/// </summary>
public class ToolsRegistry : Singleton<ToolsRegistry>
{
    public class ToolInfo
    {
        public IMcpTool ToolInstance; // 可为 null，仅用于声明
        public string Description;
        public string InputSchemaJson; // JSON Schema 文本
    }

    private readonly Dictionary<string, ToolInfo> _tools = new Dictionary<string, ToolInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new object();

    public void RegisterTool(string name, IMcpTool toolInstance, string inputSchemaJson, string description = null)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
        lock (_lock)
        {
            _tools[name] = new ToolInfo { ToolInstance = toolInstance, Description = description, InputSchemaJson = inputSchemaJson };
        }
    }

    public bool TryGetTool(string name, out ToolInfo info)
    {
        lock (_lock)
        {
            return _tools.TryGetValue(name, out info);
        }
    }

    // 返回给宿主 (MCP Host) 的 tools 列表：每项包含 name, description, inputSchema (作为 JSON object)
    public List<object> GetToolsForHost()
    {
        var list = new List<object>();
        lock (_lock)
        {
            foreach (var kv in _tools)
            {
                JObject schemaObj = null;
                try { schemaObj = !string.IsNullOrEmpty(kv.Value.InputSchemaJson) ? JObject.Parse(kv.Value.InputSchemaJson) : null; } catch { schemaObj = null; }
                list.Add(new { name = kv.Key, description = kv.Value.Description, inputSchema = schemaObj });
            }
        }
        return list;
    }

    // 返回给 LLM 的 tools 列表：每项包含 name, description, parameters (JSON Schema object)
    public List<object> GetToolsForLlm()
    {
        var list = new List<object>();
        lock (_lock)
        {
            foreach (var kv in _tools)
            {
                JObject schemaObj = null;
                try { schemaObj = !string.IsNullOrEmpty(kv.Value.InputSchemaJson) ? JObject.Parse(kv.Value.InputSchemaJson) : null; } catch { schemaObj = null; }
                list.Add(new { name = kv.Key, description = kv.Value.Description, parameters = schemaObj });
            }
        }
        return list;
    }
}

