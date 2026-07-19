using System;

/// <summary>
/// 所有 MCP 工具参数对象的基类，强制要求实现参数校验。
/// </summary>
public abstract class McpArgsBase
{
    public abstract bool Validate(out string errorMessage);
}

/// <summary>
/// 工具执行结果显式携带错误状态，避免通过字符串前缀猜测成功或失败。
/// </summary>
public readonly struct McpToolExecutionResult
{
    public string Message { get; }
    public bool IsError { get; }

    private McpToolExecutionResult(string message, bool isError)
    {
        Message = message;
        IsError = isError;
    }

    public static McpToolExecutionResult Success(string message) => new McpToolExecutionResult(message, false);
    public static McpToolExecutionResult Failure(string message) => new McpToolExecutionResult(message, true);
}

public interface IMcpTool
{
    McpToolExecutionResult Execute(string argumentsJson);
}