using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class UnityGatewayClient : IDisposable
{
    private const int ReceiveBufferSize = 8192;
    private const int MaximumMessageBytes = 1024 * 1024;

    private readonly Uri _endpoint;
    private readonly string _instanceId;
    private readonly Func<List<UnityGatewayToolDefinition>> _toolsProvider;
    private readonly Func<List<string>> _npcProvider;
    private readonly ReconnectPolicy _reconnectPolicy;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _conversationTimeout;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending =
        new ConcurrentDictionary<string, TaskCompletionSource<string>>();
    private readonly object _lifecycleLock = new object();

    private CancellationTokenSource _lifetimeCts;
    private Task _connectionLoopTask;
    private ClientWebSocket _socket;
    private string _registrationRequestId;
    private volatile bool _isRegistered;
    private bool _disposed;

    public event Action<UnityToolCommand> ToolCallReceived;
    public event Action<string> ToolCancellationReceived;
    public event Action<string> Info;
    public event Action<string> Warning;
    public event Action Registered;
    public event Action<UnityGatewayAssistantStatus> AssistantStatusReceived;
    public event Action<UnityGatewayAssistantDelta> AssistantDeltaReceived;

    public bool IsRegistered => _isRegistered;

    public UnityGatewayClient(
        string endpoint,
        string instanceId,
        Func<List<UnityGatewayToolDefinition>> toolsProvider,
        Func<List<string>> npcProvider,
        ReconnectPolicy reconnectPolicy = null,
        TimeSpan? requestTimeout = null,
        TimeSpan? conversationTimeout = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _endpoint))
            throw new ArgumentException("Unity Gateway WebSocket 地址无效。", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Unity instanceId 不能为空。", nameof(instanceId));

        _instanceId = instanceId;
        _toolsProvider = toolsProvider ?? throw new ArgumentNullException(nameof(toolsProvider));
        _npcProvider = npcProvider ?? throw new ArgumentNullException(nameof(npcProvider));
        _reconnectPolicy = reconnectPolicy ?? new ReconnectPolicy();
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        _conversationTimeout = conversationTimeout ?? TimeSpan.FromSeconds(120);
    }

    public void Start(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (_connectionLoopTask != null)
                return;

            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _connectionLoopTask = RunConnectionLoopAsync(_lifetimeCts.Token);
        }
    }


    public async Task<UnityGatewayConversationStartResult> StartConversationAsync(
        string playerId,
        string npcId,
        CancellationToken cancellationToken)
    {
        string requestId = $"conversation-start-{Guid.NewGuid():N}";
        string result = await SendRequestAsync(
            requestId,
            UnityGatewayProtocol.ConversationStartMethod,
            new { playerId, npcId },
            _requestTimeout,
            cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<UnityGatewayConversationStartResult>(result);
    }

    public async Task<UnityGatewayAssistantReply> SendPlayerMessageAsync(
        string sessionId,
        string text,
        CancellationToken cancellationToken)
    {
        string requestId = $"player-message-{Guid.NewGuid():N}";
        string result = await SendRequestAsync(
            requestId,
            UnityGatewayProtocol.PlayerMessageMethod,
            new { type = "player.message", sessionId, text },
            _conversationTimeout,
            cancellationToken).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<UnityGatewayAssistantReply>(result);
    }

    public Task EndConversationAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_isRegistered)
            return Task.CompletedTask;
        return SendNotificationSafelyAsync(new
        {
            jsonrpc = "2.0",
            method = UnityGatewayProtocol.ConversationEndMethod,
            @params = new { sessionId }
        }, cancellationToken);
    }

    private async Task<string> SendRequestAsync(
        string requestId,
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new ArgumentException("JSON-RPC 请求 ID 不能为空。", nameof(requestId));

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
            throw new InvalidOperationException($"重复的 JSON-RPC 请求 ID: {requestId}");

        try
        {
            await SendJsonAsync(new
            {
                jsonrpc = "2.0",
                method,
                id = requestId,
                @params = parameters
            }, cancellationToken).ConfigureAwait(false);

            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task timeoutTask = Task.Delay(timeout, timeoutCts.Token);
                Task completedTask = await Task.WhenAny(completion.Task, timeoutTask).ConfigureAwait(false);
                if (completedTask == completion.Task)
                {
                    timeoutCts.Cancel();
                    return await completion.Task.ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"等待 Gateway 请求结果超时: method={method}, id={requestId}");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }
    public Task SendToolResultAsync(
        string requestId,
        string message,
        bool isError,
        string errorCode,
        JToken data,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(requestId))
            return Task.CompletedTask;

        var result = new UnityGatewayToolResult
        {
            Ok = !isError,
            ErrorCode = isError ? (errorCode ?? "TOOL_EXECUTION_FAILED") : null,
            Message = message,
            Data = data
        };
        return SendJsonAsync(new { jsonrpc = "2.0", id = requestId, result }, cancellationToken);
    }

    public Task NotifyNpcChangedAsync(string npcId, bool online, CancellationToken cancellationToken)
    {
        if (!_isRegistered || string.IsNullOrWhiteSpace(npcId))
            return Task.CompletedTask;

        return SendNotificationSafelyAsync(new
        {
            jsonrpc = "2.0",
            method = UnityGatewayProtocol.NpcChangedMethod,
            @params = new { instanceId = _instanceId, npcId, online }
        }, cancellationToken);
    }

    public Task NotifyToolsChangedAsync(CancellationToken cancellationToken)
    {
        if (!_isRegistered)
            return Task.CompletedTask;

        return SendNotificationSafelyAsync(new
        {
            jsonrpc = "2.0",
            method = UnityGatewayProtocol.ToolsChangedMethod,
            @params = new { instanceId = _instanceId, tools = _toolsProvider() }
        }, cancellationToken);
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            ClientWebSocket socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            bool registeredDuringConnection = false;

            try
            {
                await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
                _socket = socket;
                Info?.Invoke($"WebSocket 已连接: {_endpoint}");

                Task receiveTask = ReceiveLoopAsync(socket, cancellationToken);
                await SendRegistrationAsync(cancellationToken).ConfigureAwait(false);
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                registeredDuringConnection = _isRegistered;
                Warning?.Invoke($"连接中断: {ex.Message}");
            }
            finally
            {
                registeredDuringConnection |= _isRegistered;
                _isRegistered = false;
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                FailAllPending(new IOException("Unity Gateway 连接已断开。"));
                socket.Abort();
                socket.Dispose();
            }

            if (cancellationToken.IsCancellationRequested)
                break;

            consecutiveFailures = registeredDuringConnection ? 1 : consecutiveFailures + 1;
            TimeSpan delay = _reconnectPolicy.GetDelay(consecutiveFailures);
            Info?.Invoke($"将在 {delay.TotalSeconds:0.#} 秒后重连 Unity Gateway。");
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            string message = await ReceiveCompleteTextMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message == null)
                throw new IOException("Unity Gateway 服务端关闭了连接。");
            await HandleMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReceiveCompleteTextMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        using (var stream = new MemoryStream())
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Unity Gateway 只接受 WebSocket 文本消息。");

                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaximumMessageBytes)
                    throw new InvalidDataException("Unity Gateway 消息超过 1 MiB 限制。");
            }
            while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken cancellationToken)
    {
        JObject message = JObject.Parse(json);
        string method = message["method"]?.Value<string>();
        if (string.IsNullOrEmpty(method))
        {
            await HandleResponseAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (method)
        {
            case UnityGatewayProtocol.ToolExecuteMethod:
                ToolCallReceived?.Invoke(ParseGatewayToolCommand(message));
                break;
            case UnityGatewayProtocol.ToolCancelMethod:
                var cancel = message["params"]?.ToObject<UnityGatewayToolCancelParams>();
                if (!string.IsNullOrWhiteSpace(cancel?.RequestId))
                    ToolCancellationReceived?.Invoke(cancel.RequestId);
                break;
            case UnityGatewayProtocol.AssistantStatusMethod:
                var status = message["params"]?.ToObject<UnityGatewayAssistantStatus>();
                if (status != null)
                    AssistantStatusReceived?.Invoke(status);
                break;
            case UnityGatewayProtocol.AssistantDeltaMethod:
                var delta = message["params"]?.ToObject<UnityGatewayAssistantDelta>();
                if (delta != null)
                    AssistantDeltaReceived?.Invoke(delta);
                break;
            default:
                Warning?.Invoke($"未处理的 Unity Gateway 方法: {method}");
                break;
        }
    }

    private async Task HandleResponseAsync(JObject message, CancellationToken cancellationToken)
    {
        string responseId = message["id"]?.Value<string>();
        if (string.IsNullOrEmpty(responseId))
            return;

        if (responseId == _registrationRequestId)
        {
            JToken error = message["error"];
            if (error != null)
                throw new InvalidOperationException($"Unity Gateway 注册失败: {error["message"]}");

            _isRegistered = message["result"]?["accepted"]?.Value<bool>() == true;
            if (!_isRegistered)
                throw new InvalidOperationException("Unity Gateway 拒绝了实例注册。");

            Info?.Invoke($"注册成功: instanceId={_instanceId}, protocolVersion={UnityGatewayProtocol.Version}");
            await SendCapabilitySnapshotAsync(cancellationToken).ConfigureAwait(false);
            Registered?.Invoke();
            return;
        }

        if (!_pending.TryRemove(responseId, out TaskCompletionSource<string> completion))
            return;

        JToken responseError = message["error"];
        if (responseError != null)
        {
            int code = responseError["code"]?.Value<int>() ?? 0;
            string remoteMessage = responseError["message"]?.Value<string>() ?? "未知错误";
            completion.TrySetException(new UnityGatewayRequestException(code, remoteMessage));
            return;
        }
        completion.TrySetResult(message["result"]?.ToString(Formatting.None));
    }


    private static UnityToolCommand ParseGatewayToolCommand(JObject message)
    {
        UnityGatewayToolExecuteParams parameters = message["params"]?.ToObject<UnityGatewayToolExecuteParams>();
        if (parameters?.Arguments == null)
            throw new JsonException("unity.tool.execute params 缺失或 arguments 不是对象。");

        return new UnityToolCommand
        {
            NpcId = parameters.NpcId,
            RequestId = message["id"]?.Value<string>(),
            Function = new UnityToolFunction
            {
                Name = parameters.Tool,
                ArgumentsJson = parameters.Arguments.ToString(Formatting.None)
            }
        };
    }

    private Task SendRegistrationAsync(CancellationToken cancellationToken)
    {
        _registrationRequestId = $"register-{Guid.NewGuid():N}";
        return SendJsonAsync(new
        {
            jsonrpc = "2.0",
            id = _registrationRequestId,
            method = UnityGatewayProtocol.RegisterMethod,
            @params = new UnityGatewayRegistration
            {
                ProtocolVersion = UnityGatewayProtocol.Version,
                InstanceId = _instanceId,
                Tools = _toolsProvider(),
                Npcs = _npcProvider()
            }
        }, cancellationToken);
    }

    private async Task SendCapabilitySnapshotAsync(CancellationToken cancellationToken)
    {
        await SendJsonAsync(new
        {
            jsonrpc = "2.0",
            method = UnityGatewayProtocol.ToolsChangedMethod,
            @params = new { instanceId = _instanceId, tools = _toolsProvider() }
        }, cancellationToken).ConfigureAwait(false);

        foreach (string npcId in _npcProvider())
        {
            await SendJsonAsync(new
            {
                jsonrpc = "2.0",
                method = UnityGatewayProtocol.NpcChangedMethod,
                @params = new { instanceId = _instanceId, npcId, online = true }
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendNotificationSafelyAsync(object payload, CancellationToken cancellationToken)
    {
        try
        {
            await SendJsonAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"发送能力变更通知失败，将在重连注册时恢复: {ex.Message}");
        }
    }

    private async Task SendJsonAsync(object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClientWebSocket socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
                throw new IOException("Unity Gateway WebSocket 未连接。");

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void FailAllPending(Exception exception)
    {
        foreach (KeyValuePair<string, TaskCompletionSource<string>> item in _pending)
        {
            if (_pending.TryRemove(item.Key, out TaskCompletionSource<string> completion))
                completion.TrySetException(exception);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(UnityGatewayClient));
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifetimeCts?.Cancel();
            _socket?.Abort();
            FailAllPending(new ObjectDisposedException(nameof(UnityGatewayClient)));
            _lifetimeCts?.Dispose();
        }
    }
}
