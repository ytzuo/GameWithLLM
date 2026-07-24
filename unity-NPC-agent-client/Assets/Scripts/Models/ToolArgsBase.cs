using System;
using Newtonsoft.Json.Linq;

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
    public JToken Data { get; }
    public bool IsError { get; }
    public string ErrorCode { get; }

    private ToolExecutionResult(string message, JToken data, bool isError, string errorCode)
    {
        Message = message;
        Data = data;
        IsError = isError;
        ErrorCode = errorCode;
    }

    public static ToolExecutionResult Success(string message) =>
        new ToolExecutionResult(message, null, false, null);

    public static ToolExecutionResult Success(JToken data, string message = null) =>
        new ToolExecutionResult(message, data, false, null);

    public static ToolExecutionResult Failure(string errorCode, string message) =>
        new ToolExecutionResult(message, null, true, errorCode);
}
