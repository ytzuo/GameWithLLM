using System;

public abstract class ToolArgsBase
{
    public abstract bool Validate(out string errorMessage);
}

public sealed class ToolExecutionException : Exception
{
    public string ErrorCode { get; }

    public ToolExecutionException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}

public readonly struct ToolExecutionResult
{
    public string Message { get; }
    public bool IsError { get; }
    public string ErrorCode { get; }

    private ToolExecutionResult(string message, bool isError, string errorCode)
    {
        Message = message;
        IsError = isError;
        ErrorCode = errorCode;
    }

    public static ToolExecutionResult Success(string message) => new ToolExecutionResult(message, false, null);
    public static ToolExecutionResult Failure(string errorCode, string message) => new ToolExecutionResult(message, true, errorCode);
}
