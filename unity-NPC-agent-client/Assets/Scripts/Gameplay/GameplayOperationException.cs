using System;
using Newtonsoft.Json.Linq;

public sealed class GameplayOperationException : Exception
{
    public string ErrorCode { get; }
    public new JToken Data { get; }

    public GameplayOperationException(string errorCode, string message, JToken data = null)
        : base(message)
    {
        ErrorCode = errorCode;
        Data = data;
    }
}
