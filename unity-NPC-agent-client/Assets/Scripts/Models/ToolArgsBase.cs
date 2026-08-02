using System;
using Newtonsoft.Json.Linq;

public abstract class ToolArgsBase
{
    public abstract bool Validate(out string errorMessage);
}

public sealed class ToolExecutionException : Exception
{
    public string ErrorCode { get; }
    public new JToken Data { get; }

    public ToolExecutionException(string errorCode, string message, JToken data = null) : base(message)
    {
        ErrorCode = errorCode;
        Data = data;
    }
}
