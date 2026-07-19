using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public class McpAsyncClient : Singleton<McpAsyncClient>
{
    [Header("网络配置")]
    public string mcpHostWsUrl = "ws://127.0.0.1:8080/unity/ws";
    public string llmApiUrl = "https://api.siliconflow.cn/v1";
    public string llmModel = "deepseek-ai/DeepSeek-V4-Pro";
    public string llmApiKey = "YOUR_API_KEY";

    private ClientWebSocket _webSocket;
    private HttpClient _httpClient;
    private CancellationTokenSource _appCts = new CancellationTokenSource();

    // 核心黑科技：用来将 WebSocket 的异步回调转为 await 等待
    // Key: callId, Value: TaskCompletionSource (等待工具返回的 JSON 结果)
    private Dictionary<string, TaskCompletionSource<string>> _pendingToolCalls = new Dictionary<string, TaskCompletionSource<string>>();

    // 模拟玩家 UI 输入的等待源
    private TaskCompletionSource<string> _playerInputTcs;

    protected override void Init()
    {
        var config = DotEnvConfig.Load();
        mcpHostWsUrl = config.Get("UNITY_JSONRPC_WS_URL", mcpHostWsUrl);
        llmApiUrl = config.Get("LLM_API_URL", llmApiUrl);
        llmModel = config.Get("LLM_MODEL", llmModel);
        llmApiKey = config.Get("OPENAI_API_KEY", string.Empty);

        _httpClient = new HttpClient();
        if (!string.IsNullOrWhiteSpace(llmApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {llmApiKey}");
            Debug.Log("[MCP Client] httpClient created, configs:");
            Debug.Log("[MCP Client] llmApiUrl: " + llmApiUrl);
            Debug.Log("[MCP Client] llmModel: " + llmModel);
            Debug.Log("[MCP Client] llmApiKey: " + llmApiKey);
        }
        else
        {
            Debug.LogWarning("[MCP Client] OPENAI_API_KEY 未配置，LLM 请求会失败。请在 monorepo 根目录 .env.local 中配置。");
        }

        // 1. 初始化并连接 WebSocket
        _ = StartConnectionAsync();
    }

    
    // 1. 初始化 WebSocket 连接与接收循环
    private async Task StartConnectionAsync()
    {
        _webSocket = new ClientWebSocket();
        try
        {
            await _webSocket.ConnectAsync(new Uri(mcpHostWsUrl), _appCts.Token);
            Debug.Log("[MCP Client] WebSocket 连接宿主服务器成功！");

            // 启动后台专门监听宿主返回数据的死循环
            _ = ReceiveWebSocketLoopAsync();
        }
        catch (Exception e)
        {
            Debug.LogError($"[MCP Client] WebSocket 连接失败: {e.Message}");
        }
    }

    // 后台接收线程：只负责收数据，并唤醒挂起的工具调用
    private async Task ReceiveWebSocketLoopAsync()
    {
        var buffer = new byte[8192];
        while (_webSocket.State == WebSocketState.Open && !_appCts.Token.IsCancellationRequested)
        {
            var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _appCts.Token);
            if (result.MessageType == WebSocketMessageType.Text)
            {
                string jsonResponse = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var responseObj = JObject.Parse(jsonResponse);
                // 区分两种消息：带有 method 的为请求（宿主 -> 我们），否则为对我们先前发起请求的响应
                if (responseObj["method"] != null)
                {
                    string method = responseObj["method"].ToString();
                    try
                    {
                        if (method == "tools/list")
                        {
                            // 宿主在询问我们当前可用的工具列表（动态工具发现）
                            string reqId = responseObj["id"]?.ToString();
                            var tools = ToolsRegistry.Instance.GetToolsForHost();
                            var resp = new
                            {
                                jsonrpc = "2.0",
                                id = reqId,
                                result = new { tools = tools }
                            };
                            string respJson = JsonConvert.SerializeObject(resp) + "\n";
                            byte[] respBytes = Encoding.UTF8.GetBytes(respJson);
                            await _webSocket.SendAsync(new ArraySegment<byte>(respBytes), WebSocketMessageType.Text, true, _appCts.Token);
                        }
                        else if (method == "tools/call")
                        {
                            // 宿主要求 Unity 执行某个工具（带有 npcId）
                            string callId = responseObj["id"]?.ToString();
                            var paramsObj = responseObj["params"] as JObject;
                            string npcId = paramsObj?["npcId"]?.ToString() ?? paramsObj?["npc_id"]?.ToString();
                            string name = paramsObj?["name"]?.ToString();
                            string arguments = paramsObj?["arguments"]?.ToString();

                            var request = new LlmToolCall
                            {
                                id = npcId,
                                transactionId = callId,
                                function = new LlmFunction { name = name, arguments = arguments }
                            };

                            // 投递到主线程路由中心，由具体 NPC 在主线程消费
                            CommandDispatcher.Instance.OnReceiveNetMessage(request);
                        }
                        else
                        {
                            Debug.LogWarning($"[MCP Client] 未处理的请求方法: {method}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MCP Client] 处理宿主请求出错: {ex.Message}");
                    }
                }
                else if (responseObj["id"] != null)
                {
                    // 这是对我们之前发出的 tools/call 的回复
                    string callId = responseObj["id"].ToString();
                    string resultData = responseObj["result"]?.ToString();

                    // 唤醒对应的 await SendToolCallRequestAsync()
                    if (_pendingToolCalls.TryGetValue(callId, out var tcs))
                    {
                        tcs.TrySetResult(resultData);
                        _pendingToolCalls.Remove(callId);
                    }
                }
            }
        }
    }

    // 将 MCP 工具执行结果原路返回给宿主（由 NPC 在主线程完成动作后调用）
    public async Task SendMcpResponseAsync(string transactionId, string text, bool isError = false)
    {
        if (string.IsNullOrEmpty(transactionId)) return;
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            Debug.LogWarning("[MCP Client] WebSocket 未就绪，无法发送 MCP 响应。");
            return;
        }

        var response = new
        {
            jsonrpc = "2.0",
            id = transactionId,
            result = new
            {
                content = new[] { new { type = "text", text = text } },
                isError = isError
            }
        };

        try
        {
            string json = JsonConvert.SerializeObject(response) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _appCts.Token);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[MCP Client] 发送 MCP 响应失败: {ex.Message}");
        }
    }

    // 2. 主循环触发入口
    public void OnPlayerInteractWithNpc(string npcId)
    {
        Debug.Log($"[UI] 玩家开始与 NPC({npcId}) 交互");
        _ = StartNewSessionAsync(npcId);
    }

    // 供外部 UI 按钮调用的方法：玩家发送了文本
    public void SubmitPlayerInput(string text)
    {
        _playerInputTcs?.TrySetResult(text);
    }

    // 4. 开始新会话方法 
    private async Task StartNewSessionAsync(string npcId)
    {
        // TODO：从本地存档/内存中拉取该 NPC 的聊天历史  
        // var messageList = historyManager.LoadDialogueHistory(npcId);

        // 此处为了演示，构造初始列表
        List<LlmMessage> messageList = new List<LlmMessage>
        {
            new LlmMessage { role = "system", content = $"你现在将扮演和驱动一名Unity游戏中的NPC，接下来我可能提出要求或提问问题，你要尝试调用工具解决它们或是给我问题的答案" }
        };
        
        // UI 推送：将已有历史刷新到屏幕上（通过 ChatViewModel）
        try
        {
            foreach (var msg in messageList)
            {
                if (msg == null) continue;
                if (string.Equals(msg.role, "system", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(msg.content))
                        ChatViewModel.Instance.AddSystemMessage(msg.content);
                }
                else if (string.Equals(msg.role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(msg.content))
                        ChatViewModel.Instance.AddPlayerMessage(msg.content);
                }
                else if (string.Equals(msg.role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(msg.content))
                        ChatViewModel.Instance.AddOpponentMessage(msg.content);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[MCP Client] failed to populate UI history via ChatViewModel: {ex.Message}");
        }

        // 正式进入会话阻塞循环
        await SessionTaskAsync(npcId, messageList);
    }

    // 5. 会话任务核心循环 (极其线性的逻辑，完全没有回调地狱)
    private async Task SessionTaskAsync(string npcId, List<LlmMessage> messageList)
    {
        Debug.Log($"[MCP Client] SessionTaskAsync: {npcId}");
        bool isSessionActive = true;

        while (isSessionActive && !_appCts.Token.IsCancellationRequested)
        {
            Debug.Log($"[MCP Client] {npcId} is waiting for player input...");
            // A. 等待玩家输入 (挂起，直到 UI 调用了 SubmitPlayerInput)
            _playerInputTcs = new TaskCompletionSource<string>();
            string playerText = await _playerInputTcs.Task;
            
            // 玩家想退出对话
            if (playerText.ToLower() == "bye") break;

            messageList.Add(new LlmMessage { role = "user", content = playerText });

            bool waitingForLlm = true;

            // B. 大模型处理循环（如果模型调用了工具，需要把结果塞回去再问一次模型）
            while (waitingForLlm)
            {
                // 请求大模型
                LlmResponse llmResponse = await SendLlmRequestAsync(messageList);

                if (llmResponse.tool_calls != null && llmResponse.tool_calls.Count > 0)
                {
                    // 遇到了工具调用！
                    var toolCall = llmResponse.tool_calls[0];

                    // 将大模型的意图记录到上下文中
                    messageList.Add(new LlmMessage
                    {
                        role = "assistant",
                        content = null,
                        tool_calls = llmResponse.tool_calls
                    });

                    Debug.Log($"[LLM] 大模型请求调用工具: {toolCall.function.name}");

                    // 发送给本地 Unity/MCP 宿主执行，并挂起等待结果
                    string toolResult = await SendToolCallRequestAsync(npcId, toolCall.id, toolCall.function.name, toolCall.function.arguments);

                    // 拿到结果后，作为 tool 角色塞回列表
                    messageList.Add(new LlmMessage
                    {
                        role = "tool",
                        tool_call_id = toolCall.id,
                        content = toolResult
                    });

                    // 将工具执行结果推送到 UI（视为 System / internal 消息）
                    try
                    {
                        if (!string.IsNullOrEmpty(toolResult))
                            ChatViewModel.Instance.AddSystemMessage(FormatToolResultForDisplay(toolResult));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[MCP Client] failed to forward tool result to ChatViewModel: {ex.Message}");
                    }

                    // 此时不跳出循环，带着工具结果继续向大模型发起 HTTP 请求
                }
                else
                {
                    // 没有工具调用，得到了最终的对白
                    string finalReply = llmResponse.content;
                    messageList.Add(new LlmMessage { role = "assistant", content = finalReply });

                    Debug.Log($"[LLM] NPC回复: {finalReply}");
                    // 将最终回复推送到 UI（通过 ViewModel）
                    try
                    {
                        if (!string.IsNullOrEmpty(finalReply))
                            ChatViewModel.Instance.AddOpponentMessage(finalReply);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[MCP Client] failed to forward LLM reply to ChatViewModel: {ex.Message}");
                    }
                    // 本轮对话结束，跳出内层循环，等待玩家下一次输入
                    
                    waitingForLlm = false;
                }
            }
        }
    }

    
    // 6. 发送大模型请求 (HTTP) 
    private static string FormatToolResultForDisplay(string toolResult)
    {
        try
        {
            JObject result = JObject.Parse(toolResult);
            bool isError = result.Value<bool?>("isError") ?? false;
            string text = result["content"]?.First?["text"]?.Value<string>();

            if (string.IsNullOrWhiteSpace(text))
                return isError ? "行动执行失败。" : "行动已完成。";

            return isError ? $"行动失败：{text}" : text;
        }
        catch (JsonException)
        {
            return toolResult;
        }
    }
    private async Task<LlmResponse> SendLlmRequestAsync(List<LlmMessage> messages)
    {
        Debug.Log($"[MCP Client] SendLlmRequestAsync");
        var requestBody = new
        {
            model = llmModel,
            messages = messages,
            // 动态注入当前运行时可用的工具声明，遵循 ProjectDoc 中的规范
            tools = ToolsRegistry.Instance.GetToolsForLlm()
        };

        string jsonPayload = JsonConvert.SerializeObject(requestBody, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        Debug.Log($"[MCP Client] waiting for llm response...");
        HttpResponseMessage response = await _httpClient.PostAsync(llmApiUrl, content);
        string responseString = await response.Content.ReadAsStringAsync();
        
        // 解析标准的 OpenAI 格式响应
        var responseJson = JObject.Parse(responseString);
        Debug.Log($"[MCP Client] received response: {responseString}");
        var messageObj = responseJson["choices"][0]["message"];

        return messageObj.ToObject<LlmResponse>();
    }

    
    // 7. 发送 ToolCall 到宿主并等待 (WebSocket + TaskCompletionSource)
    private async Task<string> SendToolCallRequestAsync(string npcId, string callId, string toolName, string arguments)
    {
        // 构建带有隐式 NpcId 的 MCP 请求包
        var mcpRequest = new
        {
            jsonrpc = "2.0",
            method = "tools/call",
            id = callId,
            @params = new
            {
                npcId = npcId,
                name = toolName,
                arguments = arguments
            }
        };

        string requestJson = JsonConvert.SerializeObject(mcpRequest) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(requestJson);

        // 创建一个挂起锁
        var tcs = new TaskCompletionSource<string>();
        _pendingToolCalls[callId] = tcs;

        // 通过 WebSocket 发送出去
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _appCts.Token);

        // 神奇的一步：在这里死死等住，直到 ReceiveWebSocketLoopAsync 收到了结果并解除了 tcs
        string resultJson = await tcs.Task;

        return resultJson;
    }
    void OnDestroy()
    {
        _appCts.Cancel();
        _webSocket?.Dispose();
        _httpClient?.Dispose();
    }
}

internal sealed class DotEnvConfig
{
    private readonly Dictionary<string, string> _values;

    private DotEnvConfig(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static DotEnvConfig Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string root = FindRepoRoot();
        if (!string.IsNullOrEmpty(root))
        {
            LoadFile(Path.Combine(root, ".env"), values);
            LoadFile(Path.Combine(root, ".env.local"), values);
        }
        return new DotEnvConfig(values);
    }

    public string Get(string key, string fallback)
    {
        string envValue = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(envValue)) return envValue.Trim();
        if (_values.TryGetValue(key, out string fileValue) && !string.IsNullOrWhiteSpace(fileValue)) return fileValue.Trim();
        return fallback;
    }

    private static string FindRepoRoot()
    {
        var candidates = new List<string>
        {
            Directory.GetCurrentDirectory(),
            Application.dataPath
        };

        foreach (string candidate in candidates)
        {
            string root = WalkUp(candidate);
            if (!string.IsNullOrEmpty(root)) return root;
        }
        return null;
    }

    private static string WalkUp(string startPath)
    {
        if (string.IsNullOrEmpty(startPath)) return null;
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            string serverDir = Path.Combine(dir.FullName, "GameMCPServer");
            string unityDir = Path.Combine(dir.FullName, "unity-NPC-agent-client");
            if (Directory.Exists(serverDir) && Directory.Exists(unityDir))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static void LoadFile(string path, Dictionary<string, string> values)
    {
        if (!File.Exists(path)) return;

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;

            int separator = line.IndexOf('=');
            if (separator <= 0) continue;

            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim().Trim('"', '\'');
            if (key.Length > 0) values[key] = value;
        }
    }
}
