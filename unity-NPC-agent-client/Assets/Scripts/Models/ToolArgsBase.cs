using System;
using Newtonsoft.Json.Linq;

public abstract class ToolArgsBase
{
    public abstract bool Validate(out string errorMessage);
}

public sealed class ToolExecutionException : Exception
{
    public string ErrorCode { get; }
    public JToken Data { get; }

    public ToolExecutionException(string errorCode, string message, JToken data = null) : base(message)
    {
        ErrorCode = errorCode;
        Data = data;
    }
}

public readonly struct ToolExecutionResult
{
    public string Message { get; }
    public JToken Data { get; }
    public bool IsError { get; }
    public bool IsPending { get; }
    public string ErrorCode { get; }

    private ToolExecutionResult(string message, JToken data, bool isError, bool isPending, string errorCode)
    {
        Message = message;
        Data = data;
        IsError = isError;
        IsPending = isPending;
        ErrorCode = errorCode;
    }

    public static ToolExecutionResult Success(string message) =>
        new ToolExecutionResult(message, null, false, false, null);

    public static ToolExecutionResult Success(JToken data, string message = null) =>
        new ToolExecutionResult(message, data, false, false, null);

    public static ToolExecutionResult Pending(string message = null) =>
        new ToolExecutionResult(message, null, false, true, null);

    public static ToolExecutionResult Failure(string errorCode, string message, JToken data = null) =>
        new ToolExecutionResult(message, data, true, false, errorCode);
}