using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public sealed class ToolPackRegistrationResult
{
    public bool Registered { get; }
    public bool Idempotent { get; }
    public int ToolCount { get; }

    internal ToolPackRegistrationResult(bool registered, bool idempotent, int toolCount)
    {
        Registered = registered;
        Idempotent = idempotent;
        ToolCount = toolCount;
    }
}

// Unity 工具的唯一注册表，同时生成对外 Runtime Manifest Schema。
public class ToolsRegistry : Singleton<ToolsRegistry>
{
    private sealed class RegisteredToolPack
    {
        public string AssemblyHash;
        public string[] ToolNames;
    }

    private readonly Dictionary<string, IAgentTool> _tools =
        new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegisteredToolPack> _toolPacks =
        new Dictionary<string, RegisteredToolPack>(StringComparer.Ordinal);
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
        AgentToolDescriptor descriptor = ValidateTool(tool);

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

    // 候选工具在锁外完成完整校验；锁内只检查与当前快照的冲突并一次性提交。
    // 因此调用方永远看不到半个工具包。
    public ToolPackRegistrationResult RegisterToolPack(
        string packageId,
        string packageVersion,
        string assemblyHash,
        IReadOnlyList<IAgentTool> tools)
    {
        packageId = ValidatePackageField(packageId, nameof(packageId));
        packageVersion = ValidatePackageField(packageVersion, nameof(packageVersion));
        assemblyHash = ValidateAssemblyHash(assemblyHash);
        if (tools == null)
            throw new ArgumentNullException(nameof(tools));
        if (tools.Count == 0)
            throw new InvalidOperationException("Tool pack must contain at least one tool.");

        var candidates = new List<KeyValuePair<string, IAgentTool>>(tools.Count);
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IAgentTool tool in tools)
        {
            AgentToolDescriptor descriptor = ValidateTool(tool);
            if (!candidateNames.Add(descriptor.Name))
            {
                throw new InvalidOperationException(
                    $"Tool pack '{packageId}' contains duplicate tool name '{descriptor.Name}'.");
            }
            candidates.Add(new KeyValuePair<string, IAgentTool>(descriptor.Name, tool));
        }
        candidates.Sort((left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        string[] toolNames = candidates.Select(candidate => candidate.Key).ToArray();
        string packageKey = packageId + "\n" + packageVersion;

        lock (_lock)
        {
            if (_toolPacks.TryGetValue(packageKey, out RegisteredToolPack existingPack))
            {
                if (string.Equals(existingPack.AssemblyHash, assemblyHash, StringComparison.Ordinal) &&
                    existingPack.ToolNames.SequenceEqual(toolNames, StringComparer.Ordinal))
                {
                    return new ToolPackRegistrationResult(false, true, toolNames.Length);
                }
                throw new InvalidOperationException(
                    $"Tool pack '{packageId}' version '{packageVersion}' was already processed " +
                    "with different content.");
            }

            foreach (KeyValuePair<string, IAgentTool> candidate in candidates)
            {
                if (_tools.TryGetValue(candidate.Key, out IAgentTool existing))
                {
                    throw new InvalidOperationException(
                        $"Tool name '{candidate.Key}' from package '{packageId}' conflicts with " +
                        $"registered type '{existing.GetType().FullName}'.");
                }
            }

            foreach (KeyValuePair<string, IAgentTool> candidate in candidates)
                _tools.Add(candidate.Key, candidate.Value);
            _toolPacks.Add(packageKey, new RegisteredToolPack
            {
                AssemblyHash = assemblyHash,
                ToolNames = toolNames
            });
        }

        ToolsChanged?.Invoke();
        return new ToolPackRegistrationResult(true, false, toolNames.Length);
    }

    private static AgentToolDescriptor ValidateTool(IAgentTool tool)
    {
        if (tool == null)
            throw new ArgumentNullException(nameof(tool));
        AgentToolDescriptor descriptor;
        try
        {
            descriptor = tool.Descriptor;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Tool type '{tool.GetType().FullName}' failed to create its Descriptor.", ex);
        }
        if (descriptor == null)
            throw new InvalidOperationException(
                $"Tool type '{tool.GetType().FullName}' has no Descriptor.");
        if (string.IsNullOrWhiteSpace(descriptor.Name) ||
            !string.Equals(descriptor.Name, descriptor.Name.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Tool type '{tool.GetType().FullName}' has an invalid name.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.InputSchemaJson))
            throw new InvalidOperationException(
                $"Tool '{descriptor.Name}' has an empty inputSchema.");
        try
        {
            if (!(JToken.Parse(descriptor.InputSchemaJson) is JObject))
                throw new InvalidOperationException("inputSchema must be a JSON object.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Tool '{descriptor.Name}' has an invalid inputSchema: {ex.Message}", ex);
        }
        return descriptor;
    }

    private static string ValidatePackageField(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(character => char.IsControl(character)))
        {
            throw new ArgumentException(
                $"{fieldName} must be a non-empty, trimmed value of at most 128 characters.",
                fieldName);
        }
        return value;
    }

    private static string ValidateAssemblyHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "assemblyHash must be a 64-character SHA-256 hexadecimal value.",
                nameof(value));
        }
        return value.ToLowerInvariant();
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
