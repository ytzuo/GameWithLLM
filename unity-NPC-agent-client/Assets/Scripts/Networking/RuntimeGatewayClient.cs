using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameWithLLM.AgentRuntime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Unity 侧唯一的 Runtime Bridge 传输：主动连接 Gateway，并维护重连和调用取消。
public sealed class RuntimeGatewayClient : IRuntimeTransport, IDisposable
{
    private sealed class InvocationRoute
    {
        public ClientWebSocket Socket;
        public CancellationTokenSource Cancellation;
    }

    private const int MaximumMessageBytes = 1 << 20;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _commandSignal = new SemaphoreSlim(0);
    private readonly ReconnectPolicy _reconnectPolicy = new ReconnectPolicy();
    private readonly ConcurrentQueue<RuntimeCommand> _commands =
        new ConcurrentQueue<RuntimeCommand>();
    private readonly ConcurrentDictionary<string, InvocationRoute> _invocations =
        new ConcurrentDictionary<string, InvocationRoute>(StringComparer.Ordinal);
    private readonly object _manifestLock = new object();

    private CancellationTokenSource _lifetime;
    private ClientWebSocket _socket;
    private RuntimeManifest _manifest;
    private bool _disposed;

    public event Action<string> Info;
    public event Action<string> Warning;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public RuntimeGatewayClient(string endpoint, string token)
    {
        _endpoint = new Uri(endpoint ?? throw new ArgumentNullException(nameof(endpoint)));
        _token = string.IsNullOrWhiteSpace(token)
            ? throw new ArgumentException(
                "RUNTIME_GATEWAY_TOKEN is required.",
                nameof(token))
            : token;
    }

    // 保存初始 Manifest 并启动后台连接循环，不阻塞 Unity 主线程。
    public Task StartAsync(
        RuntimeManifest manifest,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_lifetime != null)
            throw new InvalidOperationException(
                "Runtime Gateway Client already started.");
        SetManifest(manifest);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _ = RunAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    // 始终保留最新完整 Manifest；离线时由下一次 initialize 重新发布。
    public async Task UpdateManifestAsync(
        RuntimeManifest manifest,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        SetManifest(manifest);
        ClientWebSocket socket = _socket;
        if (socket?.State != WebSocketState.Open)
            return;
        try
        {
            await SendAsync(
                socket,
                new
                {
                    jsonrpc = "2.0",
                    method = "runtime.manifest.changed",
                    @params = ToManifest(manifest)
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warning?.Invoke(
                $"Runtime manifest update will be retried after reconnect: {ex.Message}");
        }
    }

    // 从线程安全队列向上层暴露已校验的工具命令。
    public async IAsyncEnumerable<RuntimeCommand> ReadCommandsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _commandSignal.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            while (_commands.TryDequeue(out RuntimeCommand command))
                yield return command;
        }
    }

    // 结果只发送到接收该调用的 socket，避免重连后的迟到结果串入新连接。
    public async Task SendResultAsync(
        string invocationId,
        AgentToolResult result,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocationId) ||
            !_invocations.TryRemove(invocationId, out InvocationRoute route))
            return;
        try
        {
            JObject structured = CreateStructuredResult(result);
            await SendAsync(
                route.Socket,
                new
                {
                    jsonrpc = "2.0",
                    id = invocationId,
                    result = new
                    {
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = result?.Message ?? string.Empty
                            }
                        },
                        structuredContent = structured,
                        isError = structured.Value<bool?>("ok") != true
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            route.Cancellation.Dispose();
        }
    }

    public Task SendProgressAsync(
        string invocationId,
        double progress,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(invocationId) ||
            !_invocations.TryGetValue(invocationId, out InvocationRoute route))
            return Task.CompletedTask;
        return SendAsync(
            route.Socket,
            new
            {
                jsonrpc = "2.0",
                method = "runtime.progress",
                @params = new
                {
                    requestId = invocationId,
                    progress,
                    message
                }
            },
            cancellationToken);
    }

    // RunAsync 负责主动建连、initialize、接收循环和退避重连的完整生命周期。
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        int failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            bool initialized = false;
            try
            {
                await socket.ConnectAsync(_endpoint, cancellationToken)
                    .ConfigureAwait(false);
                _socket = socket;
                string initializeId = $"runtime-init-{Guid.NewGuid():N}";
                RuntimeManifest manifest = GetManifest();
                await SendAsync(
                    socket,
                    new
                    {
                        jsonrpc = "2.0",
                        id = initializeId,
                        method = "runtime.initialize",
                        @params = new
                        {
                            token = _token,
                            manifest = ToManifest(manifest)
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
                JObject response = await ReceiveObjectAsync(
                    socket,
                    cancellationToken).ConfigureAwait(false);
                if (response.Value<string>("id") != initializeId ||
                    response["error"] != null ||
                    response["result"]?["accepted"]?.Value<bool>() != true)
                {
                    throw new InvalidOperationException(
                        "Runtime Gateway rejected initialization.");
                }
                initialized = true;
                failures = 0;
                Info?.Invoke(
                    $"Runtime Gateway connected: generation=" +
                    $"{response["result"]?["connectionGeneration"]}");
                await ReceiveLoopAsync(socket, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Warning?.Invoke($"Runtime Gateway disconnected: {ex.Message}");
            }
            finally
            {
                CancelInvocations(socket);
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                socket.Abort();
                socket.Dispose();
            }

            failures = initialized ? 1 : failures + 1;
            try
            {
                await Task.Delay(
                        _reconnectPolicy.GetDelay(failures),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            JObject message = await ReceiveObjectAsync(
                socket,
                cancellationToken).ConfigureAwait(false);
            string method = message.Value<string>("method");
            if (method == "runtime.tools.call")
            {
                await QueueToolCallAsync(
                    socket,
                    message,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (method == "runtime.cancelled")
            {
                CancelInvocation(
                    message["params"]?["requestId"]?.Value<string>());
            }
        }
    }

    // 校验协议字段并剥离路由用 entityId，再将业务参数投递给主线程消费者。
    private async Task QueueToolCallAsync(
        ClientWebSocket socket,
        JObject message,
        CancellationToken cancellationToken)
    {
        string invocationId = message.Value<string>("id");
        string name = message["params"]?["name"]?.Value<string>();
        JObject arguments = message["params"]?["arguments"] as JObject;
        string entityId = arguments?["entityId"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(invocationId) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(entityId))
        {
            await SendBridgeErrorAsync(
                socket,
                invocationId,
                -32602,
                "invalid runtime.tools.call",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        JObject businessArguments = (JObject)arguments.DeepClone();
        businessArguments.Remove("entityId");
        var invocationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var route = new InvocationRoute
        {
            Socket = socket,
            Cancellation = invocationCancellation
        };
        if (!_invocations.TryAdd(invocationId, route))
        {
            invocationCancellation.Dispose();
            await SendBridgeErrorAsync(
                socket,
                invocationId,
                -32600,
                "duplicate runtime invocation id",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _commands.Enqueue(new RuntimeCommand(
            invocationId,
            entityId,
            name,
            businessArguments.ToString(Formatting.None),
            invocationCancellation.Token));
        _commandSignal.Release();
    }

    private void CancelInvocation(string invocationId)
    {
        if (string.IsNullOrWhiteSpace(invocationId) ||
            !_invocations.TryRemove(invocationId, out InvocationRoute route))
            return;
        route.Cancellation.Cancel();
        route.Cancellation.Dispose();
    }

    // 断线只取消属于该 socket 的调用，隔离新旧连接 generation。
    private void CancelInvocations(ClientWebSocket socket)
    {
        foreach (KeyValuePair<string, InvocationRoute> pair in _invocations)
        {
            if (!ReferenceEquals(pair.Value.Socket, socket) ||
                !_invocations.TryRemove(pair.Key, out InvocationRoute route))
                continue;
            route.Cancellation.Cancel();
            route.Cancellation.Dispose();
        }
    }

    private static JObject CreateStructuredResult(AgentToolResult result)
    {
        if (result == null)
        {
            return new JObject
            {
                ["ok"] = false,
                ["errorCode"] = "EMPTY_TOOL_RESULT",
                ["message"] = "Tool returned no result."
            };
        }

        var structured = new JObject { ["ok"] = result.Ok };
        if (!string.IsNullOrEmpty(result.ErrorCode))
            structured["errorCode"] = result.ErrorCode;
        if (!string.IsNullOrEmpty(result.Message))
            structured["message"] = result.Message;
        if (!string.IsNullOrWhiteSpace(result.DataJson))
        {
            try
            {
                structured["data"] = JToken.Parse(result.DataJson);
            }
            catch (JsonReaderException)
            {
                structured["ok"] = false;
                structured["errorCode"] = "INVALID_RESULT_DATA";
                structured["message"] = "Tool result data is not valid JSON.";
            }
        }
        return structured;
    }

    private void SetManifest(RuntimeManifest manifest)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (string.IsNullOrWhiteSpace(manifest.InstanceId))
            throw new ArgumentException(
                "Runtime manifest requires instanceId.",
                nameof(manifest));
        lock (_manifestLock)
        {
            _manifest = new RuntimeManifest(
                manifest.InstanceId,
                new List<string>(manifest.EntityIds),
                new List<AgentToolDescriptor>(manifest.Tools),
                manifest.Revision);
        }
    }

    private RuntimeManifest GetManifest()
    {
        lock (_manifestLock)
        {
            if (_manifest == null)
                throw new InvalidOperationException(
                    "Runtime manifest has not been initialized.");
            return new RuntimeManifest(
                _manifest.InstanceId,
                new List<string>(_manifest.EntityIds),
                new List<AgentToolDescriptor>(_manifest.Tools),
                _manifest.Revision);
        }
    }

    private static object ToManifest(RuntimeManifest manifest)
    {
        var tools = new List<object>();
        foreach (AgentToolDescriptor tool in manifest.Tools)
        {
            tools.Add(new
            {
                name = tool.Name,
                description = tool.Description,
                inputSchema = JToken.Parse(tool.InputSchemaJson)
            });
        }
        return new
        {
            instanceId = manifest.InstanceId,
            revision = manifest.Revision,
            entities = manifest.EntityIds,
            tools
        };
    }

    private Task SendBridgeErrorAsync(
        ClientWebSocket socket,
        string invocationId,
        int code,
        string message,
        CancellationToken cancellationToken) =>
        SendAsync(
            socket,
            new
            {
                jsonrpc = "2.0",
                id = invocationId,
                error = new { code, message }
            },
            cancellationToken);

    private static async Task<JObject> ReceiveObjectAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using (var stream = new MemoryStream())
        {
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new IOException(
                        "Runtime Gateway closed the connection.");
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException(
                        "Runtime Gateway requires text messages.");
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaximumMessageBytes)
                    throw new InvalidDataException(
                        "Runtime Gateway message exceeds 1 MiB.");
            }
            while (!result.EndOfMessage);
            return JObject.Parse(Encoding.UTF8.GetString(
                stream.GetBuffer(),
                0,
                checked((int)stream.Length)));
        }
    }

    // 所有 WebSocket 写入共用发送锁，并拒绝向已被替换的 socket 写入。
    private async Task SendAsync(
        ClientWebSocket socket,
        object value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            JsonConvert.SerializeObject(value));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_socket, socket) ||
                socket.State != WebSocketState.Open)
                throw new IOException(
                    "Runtime Gateway connection is unavailable.");
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RuntimeGatewayClient));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime?.Cancel();
        ClientWebSocket socket = _socket;
        if (socket != null)
            CancelInvocations(socket);
        _socket?.Abort();
        _lifetime?.Dispose();
        _sendLock.Dispose();
        _commandSignal.Dispose();
    }
}
