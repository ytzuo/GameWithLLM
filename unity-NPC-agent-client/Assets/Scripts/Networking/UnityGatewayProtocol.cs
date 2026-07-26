using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class UnityGatewayRequestException : Exception
{
    public int Code { get; }
    public string RemoteMessage { get; }

    public UnityGatewayRequestException(int code, string remoteMessage)
        : base($"Unity Gateway 请求失败 ({code}): {remoteMessage}")
    {
        Code = code;
        RemoteMessage = remoteMessage;
    }
}

public static class UnityGatewayProtocol
{
    public const int Version = 2;
    public const string RegisterMethod = "unity.register";
    public const string NpcChangedMethod = "unity.npc.changed";
    public const string ToolsChangedMethod = "unity.tools.changed";
    public const string ToolExecuteMethod = "unity.tool.execute";
    public const string ToolCancelMethod = "unity.tool.cancel";
    public const string ConversationStartMethod = "conversation.start";
    public const string PlayerMessageMethod = "player.message";
    public const string ConversationEndMethod = "conversation.end";
    public const string SavegameConversationSaveMethod = "savegame.conversations.save";
    public const string SavegameConversationLoadMethod = "savegame.conversations.load";
    public const string AssistantStatusMethod = "assistant.status";
    public const string AssistantDeltaMethod = "assistant.delta";
}

[Serializable]
public sealed class UnityGatewayToolDefinition
{
    [JsonProperty("name")] public string Name;
    [JsonProperty("description")] public string Description;
    [JsonProperty("inputSchema")] public JObject InputSchema;
}

[Serializable]
public sealed class UnityGatewayCapabilitySnapshot
{
    [JsonProperty("tools")] public List<UnityGatewayToolDefinition> Tools;
    [JsonProperty("npcs")] public List<string> Npcs;
    [JsonProperty("npcTools")] public Dictionary<string, List<string>> NpcTools;
}

[Serializable]
public sealed class UnityGatewayRegistration
{
    [JsonProperty("protocolVersion")] public int ProtocolVersion;
    [JsonProperty("instanceId")] public string InstanceId;
    [JsonProperty("tools")] public List<UnityGatewayToolDefinition> Tools;
    [JsonProperty("npcs")] public List<string> Npcs;
    [JsonProperty("npcTools")] public Dictionary<string, List<string>> NpcTools;
}

[Serializable]
public sealed class UnityGatewayToolExecuteParams
{
    [JsonProperty("npcId")] public string NpcId;
    [JsonProperty("tool")] public string Tool;
    [JsonProperty("arguments")] public JObject Arguments;
}

[Serializable]
public sealed class UnityGatewayToolCancelParams
{
    [JsonProperty("requestId")] public string RequestId;
}
[Serializable]
public sealed class UnityGatewayToolResult
{
    [JsonProperty("ok")] public bool Ok;
    [JsonProperty("errorCode", NullValueHandling = NullValueHandling.Ignore)] public string ErrorCode;
    [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)] public string Message;
    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public JToken Data;
}
[Serializable]
public sealed class UnityGatewayConversationStartResult
{
    [JsonProperty("sessionId")] public string SessionId;
    [JsonProperty("npcId")] public string NpcId;
}

[Serializable]
public sealed class UnityGatewayAssistantReply
{
    [JsonProperty("type")] public string Type;
    [JsonProperty("sessionId")] public string SessionId;
    [JsonProperty("npcId")] public string NpcId;
    [JsonProperty("text")] public string Text;
}

[Serializable]
public sealed class UnityGatewayAssistantStatus
{
    [JsonProperty("type")] public string Type;
    [JsonProperty("sessionId")] public string SessionId;
    [JsonProperty("status")] public string Status;
}

[Serializable]
public sealed class UnityGatewayAssistantDelta
{
    [JsonProperty("type")] public string Type;
    [JsonProperty("sessionId")] public string SessionId;
    [JsonProperty("text")] public string Text;
    [JsonProperty("reset")] public bool Reset;
}

[Serializable]
public sealed class UnityGatewayConversationSaveResult
{
    [JsonProperty("ok")] public bool Ok;
    [JsonProperty("errorCode")] public string ErrorCode;
    [JsonProperty("message")] public string Message;
    [JsonProperty("saveId")] public string SaveId;
    [JsonProperty("operationId")] public string OperationId;
    [JsonProperty("contextCount")] public int ContextCount;
    [JsonProperty("savedAt")] public DateTime SavedAt;
}

[Serializable]
public sealed class UnityGatewayVisibleMessage
{
    [JsonProperty("index")] public int Index;
    [JsonProperty("role")] public string Role;
    [JsonProperty("text")] public string Text;
}

[Serializable]
public sealed class UnityGatewayLoadedConversationContext
{
    [JsonProperty("npcId")] public string NpcId;
    [JsonProperty("sessionId")] public string SessionId;
    [JsonProperty("visibleMessages")] public List<UnityGatewayVisibleMessage> VisibleMessages;
}

[Serializable]
public sealed class UnityGatewayConversationLoadResult
{
    [JsonProperty("ok")] public bool Ok;
    [JsonProperty("errorCode")] public string ErrorCode;
    [JsonProperty("message")] public string Message;
    [JsonProperty("saveId")] public string SaveId;
    [JsonProperty("contexts")] public List<UnityGatewayLoadedConversationContext> Contexts;
    [JsonProperty("loadedAt")] public DateTime LoadedAt;
}