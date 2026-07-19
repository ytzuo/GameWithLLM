using System;

[Serializable]
public sealed class UnityToolCommand
{
    // NpcId 标识命令的目标 NPC。
    public string NpcId;
    // RequestId 是 Gateway 的 JSON-RPC 请求 ID，用于原路回传执行结果。
    public string RequestId;
    public UnityToolFunction Function;
}

[Serializable]
public sealed class UnityToolFunction
{
    public string Name;
    public string ArgumentsJson;
}