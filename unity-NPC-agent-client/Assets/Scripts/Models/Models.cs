using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class LlmMessage
{
    public string role;
    public string content;
    public string tool_call_id; // 只有 role 为 tool 时需要
    public List<LlmToolCall> tool_calls; // 只有 role 为 assistant 且调用工具时需要
}

[Serializable]
public class McpToolCallResponse
{
    public string CallId;
    public bool IsExecutionError;
    public List<ToolResponseContent> ExecutionResults;

    public McpToolCallResponse(string callId, bool isExecutionError = false)
    {
        this.CallId = callId;
        this.IsExecutionError = isExecutionError;
        this.ExecutionResults = new List<ToolResponseContent>();
    }
}

[Serializable]
public class ToolResponseContent
{
    public string Type; // 固定为 "text"
    public string Text; // 真实的反馈文本，例如 "[Unity]: 成功拿到1980步枪"

    public ToolResponseContent(string text, string type = "text")
    {
        this.Type = type;
        this.Text = text;
    }
}

[Serializable]
public class LlmToolCall
{
    // id 用作目标 NPC 的标识符（npcId）
    public string id;
    // transactionId 保存 MCP 通信层的事务 ID（用于回传结果时原路返回）
    public string transactionId;
    public string type = "function";
    public LlmFunction function;
}

[Serializable]
public class LlmFunction
{
    public string name;
    public string arguments;
}

[Serializable]
public class LlmResponse
{
    public string content;
    public List<LlmToolCall> tool_calls;
}