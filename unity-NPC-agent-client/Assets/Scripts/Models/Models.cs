using System;
using System.Threading;

public sealed class AgentToolCommand
{
    public string EntityId;
    public string RequestId;
    public AgentToolFunction Function;
    public Action<ToolExecutionResult> Completion;
    public Action<double, string> Progress;
    private int _completed;

    public bool TryComplete(ToolExecutionResult result)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return false;
        Completion?.Invoke(result);
        return true;
    }
}

[Serializable]
public sealed class AgentToolFunction
{
    public string Name;
    public string ArgumentsJson;
}
