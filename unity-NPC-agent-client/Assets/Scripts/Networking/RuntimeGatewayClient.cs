using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public sealed class RuntimeManifestSnapshot
{
    public string InstanceId;
    public long Revision;
    public List<string> Entities = new List<string>();
    public List<RuntimeToolDefinition> Tools = new List<RuntimeToolDefinition>();
}

public sealed class RuntimeGatewayClient : IDisposable
{
    private const int MaximumMessageBytes = 1 << 20;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly Func<RuntimeManifestSnapshot> _manifestProvider;
    private readonly CommandDispatcher _dispatcher;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly ReconnectPolicy _reconnectPolicy = new ReconnectPolicy();
    private CancellationTokenSource _lifetime;
    private ClientWebSocket _socket;
    private bool _disposed;

    public event Action<string> Info;
    public event Action<string> Warning;
    public bool IsConnected => _socket?.State == WebSocketState.Open;

    public RuntimeGatewayClient(
        string endpoint,
        string token,
        Func<RuntimeManifestSnapshot> manifestProvider,
        CommandDispatcher dispatcher)
    {
        _endpoint = new Uri(endpoint);
        _token = string.IsNullOrWhiteSpace(token)
            ? throw new ArgumentException("RUNTIME_GATEWAY_TOKEN is required.", nameof(token))
            : token;
        _manifestProvider = manifestProvider ?? throw new ArgumentNullException(nameof(manifestProvider));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (_lifetime != null)
            throw new InvalidOperationException("Runtime Gateway Client already started.");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = RunAsync(_lifetime.Token);
    }

    public async Task NotifyManifestChangedAsync(CancellationToken cancellationToken)
    {
        ClientWebSocket socket = _socket;
        if (socket?.State != WebSocketState.Open)
            return;
        RuntimeManifestSnapshot manifest = _manifestProvider();
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "runtime.manifest.changed",
            @params = ToManifest(manifest)
        }, cancellationToken).ConfigureAwait(false);
    }

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
                await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
                _socket = socket;
                string initializeId = $"runtime-init-{Guid.NewGuid():N}";
                RuntimeManifestSnapshot manifest = _manifestProvider();
                await SendAsync(socket, new
                {
                    jsonrpc = "2.0",
                    id = initializeId,
                    method = "runtime.initialize",
                    @params = new { token = _token, manifest = ToManifest(manifest) }
                }, cancellationToken).ConfigureAwait(false);
                JObject response = await ReceiveObjectAsync(socket, cancellationToken).ConfigureAwait(false);
                if (response.Value<string>("id") != initializeId || response["error"] != null ||
                    response["result"]?["accepted"]?.Value<bool>() != true)
                    throw new InvalidOperationException("Runtime Gateway rejected initialization.");
                initialized = true;
                failures = 0;
                Info?.Invoke(
                    $"Runtime Gateway connected: generation={response["result"]?["connectionGeneration"]}");
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Warning?.Invoke($"Runtime Gateway disconnected: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_socket, socket))
                    _socket = null;
                socket.Abort();
                socket.Dispose();
            }
            failures = initialized ? 1 : failures + 1;
            try
            {
                await Task.Delay(_reconnectPolicy.GetDelay(failures), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            JObject message = await ReceiveObjectAsync(socket, cancellationToken).ConfigureAwait(false);
            string method = message.Value<string>("method");
            if (method == "runtime.tools.call")
            {
                DispatchToolCall(socket, message, cancellationToken);
            }
            else if (method == "runtime.cancelled")
            {
                string requestId = message["params"]?["requestId"]?.Value<string>();
                _dispatcher.CancelRequest(requestId);
            }
        }
    }

    private void DispatchToolCall(
        ClientWebSocket socket,
        JObject message,
        CancellationToken cancellationToken)
    {
        string requestId = message.Value<string>("id");
        string name = message["params"]?["name"]?.Value<string>();
        JObject arguments = message["params"]?["arguments"] as JObject;
        string entityId = arguments?["entityId"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(requestId) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(entityId))
        {
            _ = SendBridgeErrorAsync(socket, requestId, -32602, "invalid runtime.tools.call", cancellationToken);
            return;
        }
        JObject businessArguments = (JObject)arguments.DeepClone();
        businessArguments.Remove("entityId");
        _dispatcher.Enqueue(new AgentToolCommand
        {
            EntityId = entityId,
            RequestId = requestId,
            Function = new AgentToolFunction
            {
                Name = name,
                ArgumentsJson = businessArguments.ToString(Formatting.None)
            },
            Completion = result => _ = SendToolResultAsync(socket, requestId, result, cancellationToken),
            Progress = (progress, status) => _ = SendProgressAsync(
                socket,
                requestId,
                progress,
                status,
                cancellationToken)
        });
    }

    private async Task SendToolResultAsync(
        ClientWebSocket socket,
        string requestId,
        ToolExecutionResult result,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_socket, socket) || socket.State != WebSocketState.Open)
            return;
        var structured = new JObject { ["ok"] = !result.IsError };
        if (!string.IsNullOrEmpty(result.ErrorCode))
            structured["errorCode"] = result.ErrorCode;
        if (!string.IsNullOrEmpty(result.Message))
            structured["message"] = result.Message;
        if (result.Data != null)
            structured["data"] = result.Data.DeepClone();
        await SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = requestId,
            result = new
            {
                content = new[] { new { type = "text", text = result.Message ?? string.Empty } },
                structuredContent = structured,
                isError = result.IsError
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task SendProgressAsync(
        ClientWebSocket socket,
        string requestId,
        double progress,
        string status,
        CancellationToken cancellationToken) =>
        SendAsync(socket, new
        {
            jsonrpc = "2.0",
            method = "runtime.progress",
            @params = new { requestId, progress, message = status }
        }, cancellationToken);

    private Task SendBridgeErrorAsync(
        ClientWebSocket socket,
        string requestId,
        int code,
        string message,
        CancellationToken cancellationToken) =>
        SendAsync(socket, new
        {
            jsonrpc = "2.0",
            id = requestId,
            error = new { code, message }
        }, cancellationToken);

    private static object ToManifest(RuntimeManifestSnapshot manifest) => new
    {
        instanceId = manifest.InstanceId,
        revision = manifest.Revision,
        entities = manifest.Entities,
        tools = manifest.Tools.ConvertAll(tool => new
        {
            name = tool.Name,
            description = tool.Description,
            inputSchema = tool.InputSchema
        })
    };

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
                    throw new IOException("Runtime Gateway closed the connection.");
                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Runtime Gateway requires text messages.");
                stream.Write(buffer, 0, result.Count);
                if (stream.Length > MaximumMessageBytes)
                    throw new InvalidDataException("Runtime Gateway message exceeds 1 MiB.");
            }
            while (!result.EndOfMessage);
            return JObject.Parse(Encoding.UTF8.GetString(
                stream.GetBuffer(),
                0,
                checked((int)stream.Length)));
        }
    }

    private async Task SendAsync(
        ClientWebSocket socket,
        object value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_socket, socket) || socket.State != WebSocketState.Open)
                throw new IOException("Runtime Gateway connection is unavailable.");
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime?.Cancel();
        _socket?.Abort();
        _lifetime?.Dispose();
        _sendLock.Dispose();
    }
}
