using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

[Serializable]
public sealed class AgentSnapshotSaveResult
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
public sealed class AgentVisibleMessage
{
    [JsonProperty("index")] public int Index;
    [JsonProperty("role")] public string Role;
    [JsonProperty("text")] public string Text;
}
[Serializable]
public sealed class AgentLoadedConversationContext
{
    [JsonProperty("npcId")] public string NpcId;
    [JsonProperty("sessionId")] public string ContextId;
    [JsonProperty("visibleMessages")] public List<AgentVisibleMessage> VisibleMessages;
}
[Serializable]
public sealed class AgentSnapshotLoadResult
{
    [JsonProperty("ok")] public bool Ok;
    [JsonProperty("errorCode")] public string ErrorCode;
    [JsonProperty("message")] public string Message;
    [JsonProperty("saveId")] public string SaveId;
    [JsonProperty("contexts")] public List<AgentLoadedConversationContext> Contexts;
    [JsonProperty("loadedAt")] public DateTime LoadedAt;
}
internal sealed class CoordinationEnvelope<T>
{
    [JsonProperty("state")] public string State { get; set; }
    [JsonProperty("result")] public T Result { get; set; }
}

public sealed class SaveCoordinationClient : IDisposable
{
    private readonly string _baseUrl;
    private readonly HttpClient _client;
    public SaveCoordinationClient(string baseUrl, string bearerToken, TimeSpan timeout)
    {
        _baseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
        _client = new HttpClient { Timeout = timeout };
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("A2A_BEARER_TOKEN is required.", nameof(bearerToken));
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
    }

    // prepare 成功后才 commit，使世界存档和 Agent 快照共享同一 operationId。
    public async Task<AgentSnapshotSaveResult> PrepareAndCommitAsync(
        string saveId,
        string operationId,
        string instanceId,
        string playerId,
        string mode,
        CancellationToken cancellationToken)
    {
        CoordinationEnvelope<AgentSnapshotSaveResult> prepared =
            await PostAsync<CoordinationEnvelope<AgentSnapshotSaveResult>>(
                $"{_baseUrl}/game-saves/{saveId}/agent-context:prepare",
                new { instanceId, playerId, operationId, mode },
                cancellationToken).ConfigureAwait(false);
        if (prepared?.Result == null || !prepared.Result.Ok)
            return prepared?.Result ?? new AgentSnapshotSaveResult
            {
                Ok = false,
                ErrorCode = "COORDINATION_FAILED",
                Message = "Agent snapshot prepare returned no result."
            };
        await PostAsync<object>(
            $"{_baseUrl}/game-saves/{saveId}/agent-context:commit",
            new { instanceId, playerId, operationId },
            cancellationToken).ConfigureAwait(false);
        return prepared.Result;
    }

    // 请求 Agent Service 恢复快照，并返回新创建的 Context ID 列表。
    public async Task<AgentSnapshotLoadResult> RestoreAsync(
        string saveId,
        string operationId,
        string instanceId,
        string playerId,
        IReadOnlyList<string> npcIds,
        CancellationToken cancellationToken)
    {
        CoordinationEnvelope<AgentSnapshotLoadResult> restored =
            await PostAsync<CoordinationEnvelope<AgentSnapshotLoadResult>>(
                $"{_baseUrl}/game-saves/{saveId}/agent-context:restore",
                new { instanceId, playerId, operationId, npcIds },
                cancellationToken).ConfigureAwait(false);
        return restored?.Result ?? new AgentSnapshotLoadResult
        {
            Ok = false,
            ErrorCode = "COORDINATION_FAILED",
            Message = "Agent snapshot restore returned no result."
        };
    }

    private async Task<T> PostAsync<T>(
        string url,
        object payload,
        CancellationToken cancellationToken)
    {
        string json = JsonConvert.SerializeObject(payload);
        using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
        using (HttpResponseMessage response = await _client.PostAsync(
                   url,
                   content,
                   cancellationToken).ConfigureAwait(false))
        {
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return JsonConvert.DeserializeObject<T>(body);
        }
    }

    public void Dispose() => _client.Dispose();
}
