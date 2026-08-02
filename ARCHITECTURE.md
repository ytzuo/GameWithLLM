# NPC Agent 系统架构

本文档是当前实现的架构事实源。其他文档只提供上手说明或包级说明；发生冲突
时，以代码、测试和本文档为准。

## 1. 当前架构

系统由 Unity Agent Runtime、Go Agent Service 和 Runtime Gateway 组成。
Agent Service 与 Gateway 当前装配在同一个 Go 进程中，但代码职责独立。

```mermaid
flowchart LR
    subgraph Unity["Unity Game（使用 Agent Runtime SDK 公共契约）"]
        direction TB
        UI["UI / AgentHostClient"]
        A2AClient["A2AClientAdapter"]
        RuntimeClient["RuntimeGatewayClient<br/>WebSocket Client"]
        SaveClient["SaveCoordinationClient"]
        Dispatcher["CommandDispatcher<br/>Unity 主线程"]
        Game["IAgentEntity / IAgentTool<br/>NPC / NavMesh / Inventory"]

        UI --> A2AClient
        UI --> RuntimeClient
        UI --> SaveClient
        RuntimeClient --> Dispatcher
        Dispatcher --> Game
    end

    subgraph Go["Go Process"]
        direction TB
        subgraph AgentService["Agent Service"]
            direction TB
            A2AServer["A2A Server"]
            Conversation["Conversation / LLM Tool Loop"]
            MCPAdapter["MCP Agent Runtime Adapter"]
            SaveCoordinator["Save Coordinator"]

            A2AServer --> Conversation
            Conversation --> MCPAdapter
            SaveCoordinator -->|"Snapshot / Restore"| Conversation
        end

        subgraph Gateway["Runtime Gateway"]
            direction TB
            RuntimeWS["/runtime/ws<br/>WebSocket Server"]
            Registry["Runtime Registry<br/>Manifest / Generation / Pending"]
            MCPEndpoint["可选外部 MCP Endpoint"]

            MCPAdapter --> Registry
            Registry <--> RuntimeWS
            MCPEndpoint --> Registry
        end
    end

    LLM["OpenAI-compatible LLM API"]
    ExternalMCP["外部 MCP Client（可选）"]

    A2AClient -->|"HTTP / SSE"| A2AServer
    SaveClient -->|"REST / JSON"| SaveCoordinator
    RuntimeClient -.->|"主动建立 WebSocket"| RuntimeWS
    RuntimeClient -->|"initialize / manifest.changed / progress / result"| RuntimeWS
    RuntimeWS -->|"tools.call / cancelled"| RuntimeClient
    Conversation -->|"HTTP / SSE"| LLM
    ExternalMCP -->|"MCP JSON-RPC"| MCPEndpoint
```

协议分工：

- **A2A**：玩家消息、Conversation Context、Agent Task、流式文本和取消。
- **MCP**：Agent Service 内部的工具抽象，以及可选的外部标准工具入口。
- **Runtime Bridge**：Unity Runtime 与 Gateway 之间的连接注册、工具调用、
  结果和取消。
- **Save Coordination API**：Unity 世界存档与 Agent 对话快照的协调。

Runtime Bridge 的连接模型如下：Unity 中的 `RuntimeGatewayClient` 是
WebSocket 客户端，Gateway 是 WebSocket 服务端。Unity 启动或断线重连时，
始终由 Unity 主动连接 Gateway 的 `/runtime/ws`。同机部署和跨网络部署使用
相同的实现与协议，仅连接地址、WS/WSS 和凭据不同：

```text
同机部署：ws://127.0.0.1:8080/runtime/ws
跨网络部署：wss://agent.example.com/runtime/ws
```

## 2. 网络协议

### 2.1 入口与认证

| 调用方 | 入口 | 传输与协议 | 认证 |
|---|---|---|---|
| Unity A2A Client | `POST /a2a` | A2A JSON-RPC 2.0；流式响应为 SSE | `Authorization: Bearer A2A_BEARER_TOKEN` |
| Unity Runtime | `GET /runtime/ws` WebSocket Upgrade | Runtime Bridge JSON-RPC 2.0 over WS/WSS | `runtime.initialize.params.token` |
| 外部 MCP Client | `POST /mcp/runtimes/{instanceId}` | MCP `2025-11-25` JSON-RPC 2.0 over HTTP(S) | `Authorization: Bearer MCP_GATEWAY_SERVICE_TOKEN`；拒绝带 Origin 的浏览器请求 |
| Unity Save Client | `/game-saves/{saveId}/agent-context:*` | REST/JSON over HTTP(S) | `Authorization: Bearer A2A_BEARER_TOKEN` |
| Go LLM Client | OpenAI-compatible Chat Completions endpoint | HTTP(S) JSON；支持 SSE | `Authorization: Bearer LLM_API_KEY` |

A2A Agent Card 是无认证的普通 HTTP JSON：

```text
GET /.well-known/agent-card.json
GET /.well-known/agent.json
```

健康检查使用 `GET /health`。

### 2.2 JSON-RPC 的边界

A2A、MCP 和 Runtime Bridge 都使用 JSON-RPC 2.0 信封，但它们是三种不同的
业务协议：

```json
{"jsonrpc":"2.0","id":"request-id","method":"namespace.method","params":{}}
{"jsonrpc":"2.0","id":"request-id","result":{}}
{"jsonrpc":"2.0","id":"request-id","error":{"code":-32602,"message":"invalid params"}}
```

- A2A 方法描述 Message、Task 和 Context。
- MCP 方法描述工具发现和调用。
- Runtime Bridge 方法只承载 Gateway 与 Unity Runtime 之间的工具执行交互。
- Save Coordination 使用 REST，不使用 JSON-RPC。

### 2.3 A2A 对话平面

实现的方法：

| 方法 | 作用 |
|---|---|
| `message/send` | 非流式玩家消息，返回完成的 Task |
| `message/stream` | 返回 working Task、artifact text chunk 和最终 status update |
| `tasks/cancel` | 取消当前仍在运行的 Task |

每条玩家 Message 必须在 metadata 中携带扩展：

```text
https://gamewithllm.dev/extensions/game-context/v1
```

```json
{
  "instanceId": "local-game-1-...",
  "playerId": "local-player-1",
  "agentId": "Ryan_001",
  "sceneId": "warehouse-demo"
}
```

创建 Context 时，Agent Service 要求对应 Runtime 已注册且 NPC Profile 存在。
复用 Context 时重新校验 `instanceId + playerId + agentId`，禁止跨 Runtime、
玩家或实体复用。当前 Go `Capabilities` 实现尚未用 Manifest 的实体列表核对
新 Context 的 `agentId`；不存在或离线的实体会在 Unity 执行工具时被拒绝。

`A2AClientAdapter` 将协议 DTO 转换为 SDK 事件：

- `ResponseStarted`
- `TextDelta`
- `StatusChanged`
- `ResponseCompleted`
- `ResponseFailed`

UI 只消费这些事件，不依赖 A2A JSON 字段。

### 2.4 Runtime Bridge

连接建立方向固定为 Unity → Gateway。WebSocket 建立后为全双工通道：Unity
发送注册、Manifest 更新、进度和工具结果；Gateway 通过同一连接发送工具调用
和取消通知。

| 方向 | 方法或响应 | 作用 |
|---|---|---|
| Unity → Gateway | `runtime.initialize` request | 提交 token 和完整 `RuntimeManifest` |
| Unity → Gateway | `runtime.manifest.changed` notification | 实体或工具变化后完整替换 Manifest |
| Gateway → Unity | `runtime.tools.call` request | 调用工具；`arguments` 包含已绑定的 `entityId` |
| Unity → Gateway | 同 request ID 的 result/error | 返回 MCP `CallToolResult` 或 JSON-RPC error |
| Unity → Gateway | `runtime.progress` notification | 上报在途进度 |
| Gateway → Unity | `runtime.cancelled` notification | 取消指定 request ID |

每次 Runtime 注册获得单调递增的 `connectionGeneration`。新连接会替换同
`instanceId` 的旧连接；断线会失败该连接全部 pending 调用，迟到结果不能进入
新连接。每个 Runtime 最多同时保留 32 个 pending 工具调用。

当前 Gateway 接收后忽略 `runtime.progress`，不会持久化或转发给
Conversation/LLM。这是已知限制。

### 2.5 MCP 工具平面

Agent Service 内部通过 Go `mcp.Client` 接口访问 Runtime Registry，不向自己
的 HTTP MCP 端点发起环回请求。

虚拟 MCP 入口供外部服务身份使用，当前实现：

- `initialize`
- `notifications/initialized`
- `tools/list`
- `tools/call`
- `notifications/cancelled`

`notifications/cancelled` 当前只返回 accepted；在途 `tools/call` 的实际取消
由 HTTP 请求 Context 取消传播到 Runtime Bridge。第一版不实现 MCP Resources
或 MCP Tasks。

工具只在 Unity 注册一次。`ToolsRegistry` 在业务 Schema 外层合并必填
`entityId`；Agent Service 对模型隐藏该字段，并在调用前绑定当前 A2A
`agentId`。如果调用参数包含其他实体 ID，请求会被拒绝。

当前 Manifest 提供全局工具目录；Go 尚未按 `entityId` 过滤模型可见工具。
`ToolsRegistry.GetAvailableToolNames` 生成的实体可用性没有进入网络 Manifest。
因此最终授权必须依赖 Unity 执行时对 Entity、Tool、`IsAvailable`、Schema 和
实时游戏状态的重新校验。

`CallToolResult` 映射：

- 成功：`isError=false`，结构数据位于 `structuredContent`。
- 游戏业务失败：`isError=true`，包含稳定 `errorCode`、message 和可选 data。
- JSON-RPC 信封、方法或参数错误：使用 JSON-RPC error。

### 2.6 Save Coordination

```text
POST /game-saves/{saveId}/agent-context:prepare
POST /game-saves/{saveId}/agent-context:commit
POST /game-saves/{saveId}/agent-context:restore
GET  /game-saves/{saveId}/agent-context:status
```

Unity 权威保存 Transform、NavMesh 位置和 Inventory；Agent Service 只保存非
system 的对话快照。操作通过 `saveId + operationId` 关联。restore 使用当前
Profile 和 system prompt 创建新的 A2A Context ID。存档操作不是模型工具。

## 3. 进程内契约

以下边界不经过网络，也不重复定义协议 DTO：

| 调用方 | 被调用方 | 契约 |
|---|---|---|
| Unity UI / `AgentHostClient` | `A2AClientAdapter` | SDK `AgentResponseEvent` |
| `AgentHostClient` | `IRuntimeTransport` | `RuntimeManifest`、`RuntimeCommand`、`AgentToolResult`、Progress |
| `AgentHostClient` | `CommandDispatcher` | 将 `RuntimeCommand` 投递到主线程队列并等待结果 |
| `CommandDispatcher` | `IAgentEntity` / `IAgentTool` | `AgentToolContext`、CancellationToken、每实体 FIFO |
| Go A2A Server | `ConversationService` | 已校验的 Game Context、Context ID 和流式回调 |
| `ConversationService` | `mcp.AgentRuntime` | MCP Tool、Schema 和 `CallToolResult` 语义 |
| `mcp.AgentRuntime` | Runtime Registry | 根据 `instanceId` 解析当前 `mcp.Client` |
| Runtime Registry | `runtimeSession` | Manifest 查询、工具调用、取消和断线传播 |
| Save Coordinator | `ConversationService` | SnapshotService |

工具执行链：

```text
ConversationService
  → mcp.AgentRuntime
  → Runtime Registry / runtimeSession
  → runtime.tools.call
  → RuntimeGatewayClient
  → CommandDispatcher
  → IAgentTool
  → AgentToolResult
```

## 4. 数据权威与安全边界

- LLM Key、模型请求、完整历史和 tool loop 只在 Go。
- Unity 不直接调用 LLM，也不保存 LLM Key 或模型历史。
- Unity 是 GameObject、NavMesh、Inventory、交互状态和行为结果的权威来源。
- Go 不访问 Unity 对象，不推断游戏行为是否真实完成。
- Unity API 只在 Unity 主线程执行；网络回调只投递线程安全命令或 UI 回调。
- 工具参数在协议边界上保持 JSON 对象，不二次编码为 JSON 字符串。
- 工具 Schema 只由 Unity Runtime 生成；Go 不维护第二份目录。
- NPC Profile 只包含人格、职责和静态背景，不包含实时世界状态。
- 日志只记录 ID、实体、工具名、长度、耗时、结果和错误码，不记录正文、
  完整参数、Prompt、历史或密钥。
- Runtime、A2A 和外部 MCP 分别使用独立 token；生产地址必须使用 HTTPS/WSS。

当前活动 Context 存储在 Go 内存中。文件归档只用于显式的存档协调，不是自动
长期记忆，也不在 Go 重启时自动恢复当前会话。

## 5. 主要交互流程

### 5.1 启动和注册

1. Unity 反射发现 `IAgentTool`，注册 `IAgentEntity`，生成完整 Manifest。
2. `RuntimeGatewayClient` 作为 WebSocket 客户端主动连接 Gateway 的
   `/runtime/ws`，并发送 `runtime.initialize`。
3. Gateway 验证 token，登记 `instanceId`、Manifest 和 generation。
4. Entity 或工具变化时，Unity 发送完整 `runtime.manifest.changed`。

Unity 启动时会在配置的 `UNITY_INSTANCE_ID` 后追加随机 GUID，因此每次运行
获得独立 Runtime ID。

### 5.2 普通对话

1. Unity 通过 `message/stream` 发送玩家文本和 Game Context。
2. Agent Service 创建或校验 Context，加载 NPC Profile 并调用 LLM。
3. LLM 不调用工具时，文本增量直接通过 SSE 返回。
4. A2A Adapter 转换为 SDK 响应事件，UI 更新草稿并提交最终文本。

### 5.3 带工具的对话

1. Agent Service 从当前 Runtime Manifest 取得全局工具目录并对模型隐藏
   `entityId`。
2. LLM 返回结构化 Tool Call。
3. Go 校验 Schema 和策略，绑定当前 `entityId`。
4. MCP Runtime Adapter 经 Gateway 发送 `runtime.tools.call`。
5. `RuntimeGatewayClient : IRuntimeTransport` 产生 SDK `RuntimeCommand`。
6. `CommandDispatcher` 在主线程按 `IAgentEntity` 路由并按实体串行执行。
7. `IAgentTool` 返回 `AgentToolResult`；长时移动只在到达、失败或取消后结束。
8. Go 将结构化结果写回模型上下文并继续 tool loop。
9. 最终回复通过 A2A SSE 返回 Unity。

不同实体可以并行；同一实体保持 FIFO，避免行为竞争。

### 5.4 取消和重连

A2A Task 取消沿 Go Context 传播到 LLM、在途 MCP 调用、Runtime Bridge 和
Unity CancellationToken。移动取消会重置 NavMesh path。每次调用只完成一次，
连接 generation 隔离迟到响应。

网络恢复后 `RuntimeGatewayClient` 主动重新连接 Gateway 并发布完整 Manifest；
Gateway 不复用旧连接的 pending 或能力快照。

### 5.5 保存和恢复

保存时 Unity 暂停新对话并等待当前操作退出，捕获世界状态，再调用 prepare 和
commit。恢复时 Unity 先恢复世界与实体，Agent Service 再用当前 Profile 恢复
对话快照并返回新的 Context ID。失败状态通过 status API 诊断和显式重试。

## 6. Unity Agent Runtime SDK

UPM 包位于：

```text
unity-NPC-agent-client/Packages/com.gamewithllm.agent-runtime/
```

它当前是**公共契约包**，不是包含全部网络和调度实现的完整客户端。公共类型：

- `IAgentEntity` / `IGameObjectAgentEntity`
- `IAgentTool` / `AgentToolDescriptor` / `AgentToolContext`
- `AgentToolResult`
- `IAgentMainThreadScheduler`
- `RuntimeCommand` / `RuntimeManifest`
- `IRuntimeTransport`
- `AgentResponseEvent` 及其派生事件

生产代码直接引用 `GameWithLLM.AgentRuntime` 程序集：

- `RuntimeGatewayClient` 实现 `IRuntimeTransport`。
- `NpcEntity` 实现 `IGameObjectAgentEntity`。
- `NpcTool<TArgs>` 适配 Warehouse 业务工具到 `IAgentTool`。
- `ToolsRegistry`、`CommandDispatcher` 和 `AgentHostClient` 使用 SDK 类型。

`A2AClientAdapter`、`RuntimeGatewayClient`、注册表、调度器、UI 和具体游戏
逻辑仍位于 `Assets/Scripts`，不会随当前 SDK 包发布。`Assets` 中不得重新定义
平行的 Tool、Result、Command、Manifest、Entity 或 Transport 公共类型。

## 7. 代码地图

### 7.1 Go

| 路径 | 职责 |
|---|---|
| `cmd/server/main.go` | HTTP 生命周期、Signal 和优雅关闭 |
| `internal/config` | 环境变量与根目录 dotenv |
| `internal/handler` | 路由装配和健康检查 |
| `internal/a2a` | Agent Card、A2A JSON-RPC/SSE、Game Context 校验 |
| `internal/agent` | Profile、内存 Context、LLM、tool loop 和对话归档 |
| `internal/mcp` | MCP 类型、HTTP Client、Entity 绑定和 Agent Runtime Adapter |
| `internal/gateway` | Runtime Bridge、Registry、generation 和虚拟 MCP |
| `internal/savecoord` | prepare、commit、restore、status |
| `internal/tools` | 模型工具目录、Schema 校验和策略 |

### 7.2 Unity

| 路径 | 职责 |
|---|---|
| `Assets/Scripts/Networking/AgentHostClient.cs` | 场景门面、A2A 与 Runtime 编排 |
| `Assets/Scripts/Networking/A2AClientAdapter.cs` | A2A JSON-RPC/SSE 与 SDK 事件映射 |
| `Assets/Scripts/Networking/RuntimeGatewayClient.cs` | `IRuntimeTransport` WebSocket 实现 |
| `Assets/Scripts/Networking/SaveCoordinationClient.cs` | Save Coordination REST Client |
| `Assets/Scripts/CommandDispatcher/ToolsRegistry.cs` | 工具发现、Schema 和 Manifest 工具快照 |
| `Assets/Scripts/CommandDispatcher/CommandDispatcher.cs` | 主线程 Entity 路由和每实体 FIFO |
| `Assets/Scripts/CommandDispatcher/NpcTool.cs` | Warehouse 工具适配基类 |
| `Assets/Scripts/GameLogic/NpcEntity.cs` | NPC 生命周期、NavMesh 行为和结果 |
| `Packages/com.gamewithllm.agent-runtime/Runtime` | SDK 公共契约 |

移动或重命名 Unity 资源时必须同时移动 `.meta` 并保留 GUID。

## 8. 配置

Agent Service：

- `AGENT_SERVICE_ADDR`
- `AGENT_SERVICE_BASE_URL`
- `A2A_BEARER_TOKEN`
- `RUNTIME_GATEWAY_TOKEN`
- `MCP_GATEWAY_SERVICE_TOKEN`
- `LLM_API_URL`
- `LLM_API_KEY`
- `LLM_MODEL`
- `LLM_REQUEST_TIMEOUT_SECONDS`
- `LLM_MAX_RETRIES`
- `LLM_MAX_TOOL_ROUNDS`
- `LLM_MAX_CONTEXT_CHARS`
- `CONVERSATION_SAVE_DIR`
- `NPC_PROFILE_PATH`

Unity：

- `AGENT_SERVICE_BASE_URL`
- `A2A_AGENT_URL`
- `A2A_BEARER_TOKEN`
- `RUNTIME_GATEWAY_WS_URL`
- `RUNTIME_GATEWAY_TOKEN`
- `UNITY_INSTANCE_ID`
- `PLAYER_ID`
- `UNITY_SCENE_ID`

配置优先级：进程环境变量 > `.env.local` > `.env` > 默认值。认证 token
没有可运行默认值，真实值不得提交。

## 9. 验证

Go：

```text
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

Unity 修改必须完成 C# 编译，并在 `SampleScene` 验证：

1. Runtime 注册成功。
2. 普通对话和流式回复正常。
3. `game_npc_move` 能到达 warehouse 和 gate。
4. 取消能停止当前 Task 和移动。
5. Go 重启后 Unity 能重连并重新发布 Manifest。
6. Inventory 与存档恢复正常。
7. Console 无编译错误、Missing Script、线程或协议异常。
