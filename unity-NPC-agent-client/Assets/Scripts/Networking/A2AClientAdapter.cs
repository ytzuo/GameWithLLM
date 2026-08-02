using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class A2AClientAdapter : IDisposable
{
    public const string GameContextExtension =
        "https://gamewithllm.dev/extensions/game-context/v1";

    private readonly Uri _endpoint;
    private readonly HttpClient _httpClient;
    private long _nextRequestId;
    private string _activeTaskId;

    public A2AClientAdapter(string endpoint, string bearerToken, TimeSpan timeout)
    {
        _endpoint = new Uri(endpoint ?? throw new ArgumentNullException(nameof(endpoint)));
        _httpClient = new HttpClient { Timeout = timeout };
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("A2A_BEARER_TOKEN is required.", nameof(bearerToken));
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<ResponseCompleted> SendStreamingAsync(
        string contextId,
        string instanceId,
        string playerId,
        string agentId,
        string sceneId,
        string text,
        Action<AgentResponseEvent> onEvent,
        CancellationToken cancellationToken)
    {
        string requestId = Interlocked.Increment(ref _nextRequestId).ToString();
        var message = new
        {
            messageId = $"unity-message-{Guid.NewGuid():N}",
            contextId,
            role = "user",
            parts = new[] { new { kind = "text", text } },
            metadata = new Dictionary<string, object>
            {
                [GameContextExtension] = new { instanceId, playerId, agentId, sceneId }
            }
        };
        string json = JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id = requestId,
            method = "message/stream",
            @params = new { message }
        });
        using (var request = new HttpRequestMessage(HttpMethod.Post, _endpoint))
        {
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using (HttpResponseMessage response = await _httpClient.SendAsync(
                       request,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    ResponseCompleted completed = null;
                    while (!reader.EndOfStream)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                            continue;
                        JObject envelope = JObject.Parse(line.Substring(6));
                        JToken error = envelope["error"];
                        if (error != null)
                            throw new InvalidOperationException(
                                $"A2A request failed ({error["code"]}): {error["message"]}");
                        JToken result = envelope["result"];
                        string kind = result?["kind"]?.Value<string>();
                        if (kind == "artifact-update")
                        {
                            string delta = result["artifact"]?["parts"]?[0]?["text"]?.Value<string>() ?? string.Empty;
                            bool append = result["append"]?.Value<bool>() != false;
                            onEvent?.Invoke(new TextDelta(delta, !append));
                            continue;
                        }
                        string taskId = result?["id"]?.Value<string>() ??
                                        result?["taskId"]?.Value<string>();
                        string responseContextId = result?["contextId"]?.Value<string>();
                        string state = result?["status"]?["state"]?.Value<string>();
                        if (!string.IsNullOrWhiteSpace(taskId))
                            _activeTaskId = taskId;
                        if (state == "working")
                        {
                            onEvent?.Invoke(new ResponseStarted(taskId, responseContextId));
                            onEvent?.Invoke(new StatusChanged(state));
                        }
                        else if (state == "completed")
                        {
                            string finalText = result["status"]?["message"]?["parts"]?[0]?["text"]?.Value<string>() ?? string.Empty;
                            completed = new ResponseCompleted(finalText, responseContextId);
                            onEvent?.Invoke(completed);
                            _activeTaskId = null;
                        }
                        else if (state == "failed" || state == "cancelled")
                        {
                            string messageText = result["status"]?["message"]?["parts"]?[0]?["text"]?.Value<string>() ?? state;
                            onEvent?.Invoke(new ResponseFailed(state.ToUpperInvariant(), messageText));
                            _activeTaskId = null;
                        }
                    }
                    return completed ?? throw new InvalidDataException("A2A stream ended without a completed Task.");
                }
            }
        }
    }

    public async Task CancelActiveTaskAsync(CancellationToken cancellationToken)
    {
        string taskId = _activeTaskId;
        if (string.IsNullOrWhiteSpace(taskId))
            return;
        string json = JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _nextRequestId).ToString(),
            method = "tasks/cancel",
            @params = new { id = taskId }
        });
        using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
        using (HttpResponseMessage response = await _httpClient.PostAsync(
                   _endpoint,
                   content,
                   cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
