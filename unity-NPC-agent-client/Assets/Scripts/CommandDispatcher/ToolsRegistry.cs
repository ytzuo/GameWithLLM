using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class ToolsRegistry : Singleton<ToolsRegistry>
{
    public class ToolInfo
    {
        public string Description;
        public string InputSchemaJson;
    }

    private readonly Dictionary<string, ToolInfo> _tools = new Dictionary<string, ToolInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new object();

    public event Action ToolsChanged;

    public void RegisterTool(string name, string inputSchemaJson, string description = null)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentNullException(nameof(name));

        lock (_lock)
        {
            _tools[name] = new ToolInfo
            {
                Description = description,
                InputSchemaJson = inputSchemaJson
            };
        }
        ToolsChanged?.Invoke();
    }


    public List<UnityGatewayToolDefinition> GetToolsForGateway()
    {
        var list = new List<UnityGatewayToolDefinition>();
        lock (_lock)
        {
            foreach (var pair in _tools)
            {
                list.Add(new UnityGatewayToolDefinition
                {
                    Name = pair.Key,
                    Description = pair.Value.Description,
                    InputSchema = ParseSchema(pair.Value.InputSchemaJson)
                });
            }
        }
        return list;
    }


    private static JObject ParseSchema(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
            return new JObject();
        try
        {
            return JObject.Parse(schemaJson);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ToolsRegistry] Invalid tool schema: {ex.Message}");
            return new JObject();
        }
    }
}