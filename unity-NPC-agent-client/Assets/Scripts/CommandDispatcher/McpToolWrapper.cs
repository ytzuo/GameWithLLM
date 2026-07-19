using System;
using UnityEngine;

/// <summary>
/// 负责工具参数反序列化、校验、异常隔离和错误状态标记。
/// </summary>
public class McpToolWrapper<T> : IMcpTool where T : McpArgsBase
{
    private readonly Func<T, string> _coreLogic;

    public McpToolWrapper(Func<T, string> coreLogic)
    {
        _coreLogic = coreLogic ?? throw new ArgumentNullException(nameof(coreLogic));
    }

    public McpToolExecutionResult Execute(string argumentsJson)
    {
        T parsedArgs;
        try
        {
            parsedArgs = JsonUtility.FromJson<T>(argumentsJson);
            if (parsedArgs == null)
                throw new InvalidOperationException("JSON 解析结果为空。");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Failure($"参数 JSON 格式不正确或解析失败：{ex.Message}");
        }

        if (!parsedArgs.Validate(out string validationError))
            return McpToolExecutionResult.Failure($"参数校验失败：{validationError}");

        try
        {
            return McpToolExecutionResult.Success(_coreLogic(parsedArgs));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MCP Tool] {typeof(T).Name} execution failed: {ex}");
            return McpToolExecutionResult.Failure($"游戏逻辑执行失败：{ex.Message}");
        }
    }
}