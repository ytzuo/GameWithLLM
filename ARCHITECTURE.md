# NPC Agent 系统架构

本文档是当前实现的架构事实源。系统已经完成从项目内 v2 WebSocket
协议到 A2A + MCP 分层协议的破坏性重构；旧 `/unity/ws`、旧 DTO 和双栈
兼容路径均不存在。

## 1. 总体架构

系统由 Unity Agent Runtime、Go Agent Service 和统一 Runtime Gateway
构成。本地与远程使用同一条出站 Runtime Transport，仅连接地址不同：

```text
Unity Game
├─ UI / NPC / NavMesh / Inventory / SaveGame
├─ Unity Agent Runtime SDK
├─ A2A Client ───────────────────────────► Go Agent Service
└─ RuntimeGatewayClient ────────────────► Runtime Gateway
                                            │
                                            ▼
                                      Runtime Registry
                                            │
                                            ▼
                                      MCP Tool Adapter
```

协议职责严格分层：

- A2A：玩家与 Agent 的 Message、Context、Task、流式文本、状态和取消。
- MCP：Agent Service 内部工具抽象及虚拟标准端点。
- Runtime Bridge：所有 Unity Runtime 的出站注册、工具调用、进度和取消。
- Save Coordination API：Unity 世界存档与 Agent 对话快照的 prepare、
  commit、restore 和状态查询。

## 2. 模块间协议总览

### 2.1 网络边界协议矩阵

| 调用方 | 被调用方 | 协议语义 | 传输与信封 | 认证 | 主要交互 |
|---|---|---|---|---|---|
| Unity `A2AClientAdapter` | Go A2A Server | A2A | HTTP(S) POST + JSON-RPC 2.0；流式响应使用 SSE | `Authorization: Bearer A2A_BEARER_TOKEN` | `message/send`、`message/stream`、`tasks/cancel` |
| Unity `RuntimeGatewayClient` | Runtime Gateway | 内部 Runtime Bridge | WebSocket/WSS + JSON-RPC 2.0 | `runtime.initialize.params.token` 使用 `RUNTIME_GATEWAY_TOKEN` | Runtime 注册、Manifest 更新、工具调用、进度、取消、调用结果 |
| 第三方 MCP Client | Runtime Gateway 虚拟 MCP | MCP `2025-11-25` | HTTP(S) POST + JSON-RPC 2.0 | `Authorization: Bearer MCP_GATEWAY_SERVICE_TOKEN` | `initialize`、`notifications/initialized`、`tools/list`、`tools/call`、取消 |
| Unity `SaveCoordinationClient` | Save Coordinator | 项目内 Save Coordination REST API | HTTP(S) + JSON request/response；不使用 JSON-RPC | `Authorization: Bearer A2A_BEARER_TOKEN` | `prepare`、`commit`、`restore`、`status` |
| Go LLM Client | OpenAI-compatible LLM | Chat Completions API | HTTP(S) JSON；流式响应使用 SSE，兼容非流式 JSON | `Authorization: Bearer LLM_API_KEY` | messages、tools、tool calls、文本增量 |

Agent Card 发现是普通 HTTP GET + JSON，不使用 JSON-RPC：

```text
GET /.well-known/agent-card.json
GET /.well-known/agent.json
```

### 2.2 同进程模块契约

以下边界不经过网络，也不重复序列化协议 DTO：

| 调用方 | 被调用方 | 契约 |
|---|---|---|
| Unity UI / `AgentHostClient` | `A2AClientAdapter` | C# 方法调用和 `AgentResponseEvent`：`ResponseStarted`、`TextDelta`、`StatusChanged`、`ResponseCompleted`、`ResponseFailed` |
| `RuntimeGatewayClient` | `AgentHostClient` | SDK `IRuntimeTransport`：产生 `RuntimeCommand`，接收 `RuntimeManifest`，发送 `AgentToolResult` 和 Progress |
| `AgentHostClient` | `CommandDispatcher` | SDK `RuntimeCommand` 投递到线程安全队列并异步等待 `AgentToolResult`；网络线程不得调用 Unity API |
| `CommandDispatcher` | `IAgentEntity` / `IAgentTool` | SDK Entity Registry、Tool Registry、`AgentToolContext`、CancellationToken 和按实体串行执行契约 |
| Go A2A Server | `ConversationService` | Go 方法调用；传递已验证的 Game Context、Context ID 和流式事件回调 |
| `ConversationService` | `mcp.AgentRuntime` | Go `agent.Runtime` 接口；使用 MCP Tool、Schema 和 `CallToolResult` 语义 |
| `mcp.AgentRuntime` | Runtime Registry | 按 `instanceId` 解析当前 `mcp.Client`；本地与远程没有 fallback 分支 |
| Runtime Registry | Gateway `runtimeSession` | Go `mcp.Client` 接口；`ListTools` 读取 Manifest，`CallTool` 转换为 Runtime Bridge 调用 |
| Save Coordinator | `ConversationService` | Go SnapshotService 接口；只处理 Agent 对话快照 |

因此 Agent Service 内部的工具路径是：

```text
ConversationService
  └─ agent.Runtime / mcp.AgentRuntime              （Go 接口，MCP 语义）
       └─ Runtime Registry / runtimeSession         （Go 接口）
            └─ runtime.tools.call                   （JSON-RPC 2.0 / WebSocket）
                 └─ Unity CommandDispatcher         （主线程队列）
```

Agent Service 不会为了调用同进程 Gateway 而向自己的虚拟 MCP HTTP 端点
环回请求。虚拟 MCP 端点只用于真正的外部 MCP Client。

### 2.3 JSON-RPC 的使用边界

A2A、MCP 和 Runtime Bridge 都复用 JSON-RPC 2.0 的请求/响应信封：

```json
{"jsonrpc":"2.0","id":"request-id","method":"namespace.method","params":{}}
{"jsonrpc":"2.0","id":"request-id","result":{}}
{"jsonrpc":"2.0","id":"request-id","error":{"code":-32602,"message":"invalid params"}}
```

它们共享信封格式，但不是同一个业务协议：

- A2A 方法描述玩家与 Agent 的 Message、Task 和 Context。
- MCP 方法描述标准工具发现与调用。
- Runtime Bridge 方法只负责 Gateway 与 Unity Runtime 之间的反向承载。
- Save Coordination 使用 REST/JSON，不使用 JSON-RPC。

旧 `/unity/ws` 同样曾使用 JSON-RPC 信封，但其 `unity.register`、
`player.message`、`unity.tool.execute` 等方法已经全部删除。保留 JSON-RPC
2.0 不代表保留旧 v2 协议。

### 2.4 Runtime Bridge 方法方向

| 方向 | 方法/消息 | 类型 | 作用 |
|---|---|---|---|
| Unity → Gateway | `runtime.initialize` | Request | token 认证并提交完整 Runtime Manifest |
| Unity → Gateway | `runtime.manifest.changed` | Notification | 实体或工具变化后完整替换 Manifest |
| Gateway → Unity | `runtime.tools.call` | Request | 调用指定工具，`arguments` 必须包含已绑定的 `entityId` |
| Unity → Gateway | 与请求同 `id` 的 `result/error` | Response | 返回 MCP `CallToolResult` 或 JSON-RPC error |
| Unity → Gateway | `runtime.progress` | Notification | 上报安全、限频的在途进度；当前 Gateway 不持久化，也不转发给 Conversation/LLM |
| Gateway → Unity | `runtime.cancelled` | Notification | 取消指定 `requestId` 的游戏行为 |

## 3. 不可跨越的边界

- LLM Key、模型请求、完整历史和 tool loop 只在 Go Agent Service。
- Unity 不调用 LLM，也不保存 LLM Key 或模型历史。
- Unity 是 GameObject、NavMesh、Inventory、交互和行为结果的权威来源。
- Go 不访问 Unity 对象，不推断行为是否真实完成。
- Unity API 只在 Unity 主线程执行；网络回调只投递命令或 UI 事件。
- MCP `arguments` 始终是 JSON 对象，不做 JSON 字符串二次编码。
- 工具 Schema 只由 Unity Runtime 生成；Go 不维护重复目录。
- 静态 NPC Profile 不包含坐标、路径、库存或任务进度。
- 日志不记录消息正文、模型全文、完整工具参数、Prompt 或密钥。

## 4. A2A 对话平面

Agent Service 暴露：

| 路由 | 用途 |
|---|---|
| `/.well-known/agent-card.json` | A2A Agent Card |
| `/.well-known/agent.json` | Agent Card 发现别名 |
| `/a2a` | A2A JSON-RPC binding，支持 `message/send`、`message/stream`、`tasks/cancel` |

`message/stream` 使用 SSE 返回初始 working Task、artifact text chunk 和最终
completed/failed/cancelled status-update。Unity 的 `A2AClientAdapter` 将这些 DTO
转换为 SDK 事件：

- `ResponseStarted`
- `TextDelta`
- `StatusChanged`
- `ResponseCompleted`
- `ResponseFailed`

UI 只消费这些事件，不依赖 A2A JSON 字段。

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

Agent Service 在复用 Context 时重新校验
`instanceId + playerId + agentId`，防止 Context 跨 Runtime、玩家或实体复用。
A2A 与 Save Coordination 使用短期 bearer token。

## 5. MCP 工具平面

Agent Service 通过 MCP `Client` 抽象访问 Runtime Registry；Gateway 还为
第三方服务身份暴露虚拟 MCP Server。协议版本锁定为 `2025-11-25`。

实现的方法和通知：

- `initialize`
- `notifications/initialized`
- `tools/list`
- `tools/call`
- `notifications/cancelled`
- Runtime Bridge 的安全进度通知

工具在 Runtime 中只注册一次。SDK 在业务 Schema 外层合并必填
`entityId`：

```json
{
  "name": "game_npc_move",
  "arguments": {
    "entityId": "Ryan_001",
    "targetId": "landmark:gate",
    "approachDistance": 1.5
  }
}
```

Agent Service 向模型隐藏路由字段，并在调用 MCP 前将当前已认证
`agentId` 绑定为 `entityId`。如果模型或调用方提供了不同实体，调用会被
拒绝。Unity 执行前再次检查实体在线、工具存在、`IsAvailable`、JSON
Schema、领域 Validate 和当前游戏状态。

MCP `CallToolResult` 映射：

- 成功：`isError=false`，真实结构数据放在 `structuredContent`。
- 业务失败：`isError=true`，包含稳定 `errorCode`、message 和可选 data。
- JSON-RPC 信封、方法或参数错误：JSON-RPC error。

第一版不实现 MCP Resources 和实验性 MCP Tasks。实时查询继续使用
`game_npc_get_state`、`game_scene_get_targets`、Inventory 等工具。

## 6. Unity Agent Runtime SDK

可复用 UPM 包位于：

```text
Packages/com.gamewithllm.agent-runtime/
├─ Runtime/Core
├─ Runtime/Conversations
├─ Runtime/Transports
└─ Samples~
   ├─ WarehouseDemo
   └─ SwitchDemo
```

公共契约包括：

- `IAgentEntity` / `IGameObjectAgentEntity`
- `IAgentTool` / `AgentToolDescriptor` / `AgentToolContext`
- `AgentToolResult`
- `IAgentMainThreadScheduler`
- `IRuntimeTransport`
- 统一 `AgentResponseEvent`

包不引用 `NpcEntity`、UI、SaveGame、A2A DTO、MCP DTO、Gateway DTO 或 Go
类型。Warehouse 是现有 NPC/NavMesh/Inventory 示例；SwitchDemo 展示非
NPC 游戏实体复用相同 SDK。

UPM 契约是生产运行链的唯一公共类型源，不允许在 `Assets` 中再定义平行的
Tool、Result、Command、Manifest 或 Transport 类型。当前生产链直接使用：

- `ToolsRegistry` 保存和发现 `IAgentTool`，Manifest 使用
  `AgentToolDescriptor`。
- `CommandDispatcher` 保存 `IAgentEntity`，接收 `RuntimeCommand`，
  并按实体串行执行工具。
- 工具同步或异步返回 `AgentToolResult`；长时移动的
  `ValueTask<AgentToolResult>` 只有在真实到达、失败或取消后才结束。
- `AgentHostClient` 只依赖 `IRuntimeTransport` 编排命令、结果和进度。
- `RuntimeGatewayClient` 是 `IRuntimeTransport` 的 WebSocket/WSS 实现。
- `NpcEntity` 是 `IGameObjectAgentEntity` 的 Warehouse 游戏适配实现。

SampleScene 的生产适配层：

- `ToolsRegistry`：反射发现工具、生成严格 Schema、合并 `entityId`。
- `CommandDispatcher`：`IAgentEntity` 注册、线程安全入站队列、实体路由
  和每实体 FIFO 执行。
- `NpcTool<TArgs>`：把 Warehouse 参数校验和 `NpcEntity` 能力适配到
  `IAgentTool`，不是第二套工具接口。
- `NpcEntity`：主线程执行 NavMesh 行为并完成 `AgentToolResult`。
- `RuntimeGatewayClient`：本地和远程共用的 `IRuntimeTransport` 实现。
- `A2AClientAdapter`：A2A SSE 到 UI 事件的适配。

移动工具每 0.5 秒最多报告一次不含参数或内部状态的 `moving` 进度。
取消后 NavMesh path 被重置；CancellationToken、Transport invocation route
和 Gateway connection generation 共同隔离迟到结果。

## 7. 统一 Runtime Transport

Unity 无论本地或远程都主动连接：

```text
本地：ws://127.0.0.1:8080/runtime/ws
远程：wss://agent.example.com/runtime/ws
```

首条消息必须是 `runtime.initialize`，携带短期设备 token 和完整
Runtime Manifest。Gateway 为实例分配单调递增
`connectionGeneration`。能力变化使用 `runtime.manifest.changed` 完整
替换。

本地不再启动 Unity HTTP MCP Server，也不存在本地 fallback。Agent
Service 只有在目标 `instanceId` 已注册到 Runtime Registry 后才会创建或
继续 A2A Context。网络回调始终只向 `CommandDispatcher` 投递主线程命令。

## 8. Runtime Gateway 与虚拟 MCP

Gateway 为第三方服务身份暴露：

```text
/mcp/runtimes/{instanceId}
```

该端点要求 `MCP_GATEWAY_SERVICE_TOKEN`。Gateway 只负责认证、实例路由、
pending、cancel、disconnect、generation 和 MCP 端点虚拟化；它不调用
LLM、不保存 Conversation、不运行 tool loop，也不修改 Tool Result。

断线会失败该 generation 的全部 pending 调用。重连完整发布 Manifest，
旧连接的迟到结果不会写入新连接。Registry 以 instanceId 隔离 Runtime，
`entityId` 仍必须属于目标 Manifest。

## 9. Conversation Engine

Go `internal/agent` 保留经过验证的能力：

- NPC Profile 严格加载与固定 Prompt 模板。
- 每 Context 串行处理玩家消息。
- 字符预算按完整工具轮次裁剪。
- OpenAI-compatible streaming Chat Completions。
- 临时文本草稿 reset。
- 最大 tool round 策略。
- 参数 Policy/Schema 校验。
- 结构化 Tool Result 写回模型上下文。
- 429/5xx 与未输出文本的网络重试。
- Context 取消向 LLM 和 MCP 传播。

Runtime 边界现在要求显式 `instanceId + entityId`，不再通过全局 npcId
发现 Unity 连接。

## 10. Save Coordination

端点：

```text
POST /game-saves/{saveId}/agent-context:prepare
POST /game-saves/{saveId}/agent-context:commit
POST /game-saves/{saveId}/agent-context:restore
GET  /game-saves/{saveId}/agent-context:status
```

Unity 仍权威保存 Transform、NavMesh 位置和 Inventory；Agent Service 只
保存非 system 的对话上下文。prepare 生成/覆盖 Agent 快照，commit 记录
跨权威提交完成，restore 使用当前 Profile/Prompt 创建新的 A2A Context ID。
Coordinator 状态按 `saveId + operationId` 显式诊断和重试。Save 操作不是
模型工具。

保存开始时 Unity 场景门面先拒绝新对话，并等待当前 A2A/tool loop 退出；
在同一互斥区间内捕获世界存档、prepare Agent 快照并 commit。失败的世界
存档保持 conversationSynced=false，可用同一 operationId 幂等重试。

## 11. Go 代码地图

| 路径 | 职责 |
|---|---|
| `cmd/server/main.go` | Agent Service HTTP 生命周期与优雅关闭 |
| `internal/a2a` | Agent Card、A2A JSON-RPC/SSE、Game Context 校验 |
| `internal/agent` | Profile、Context、LLM 和 tool loop |
| `internal/mcp` | MCP 类型、Streamable HTTP Client、Agent Runtime Adapter |
| `internal/gateway` | Runtime Bridge、Registry、generation、虚拟 MCP Endpoint |
| `internal/savecoord` | prepare/commit/restore/status |
| `internal/tools` | 模型可见工具、Schema 校验与策略 |
| `internal/config` | 环境变量和 dotenv |
| `internal/handler` | 新路由装配 |

`internal/unity` 已删除。

## 12. 配置

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

优先级仍为进程环境变量 > `.env.local` > `.env` > 默认值。认证 token
没有可运行默认值；本地被忽略的 `.env` 可使用随机开发 token，仓库只
提交占位符。

## 13. 安全与日志

- Runtime、A2A 和 Gateway Service 身份分别认证。
- 本地和远程 Runtime 都只建立出站 WebSocket；生产环境使用 `wss://`。
- HTTP/WebSocket 单消息限制为 1 MiB。
- MCP Gateway 端点按 instanceId 路由，Context 按三元所有权校验。
- 所有 WebSocket 写入经过发送锁。
- pending 支持 cancel、断线清理、generation 和重复完成隔离。
- 日志只记录 ID、实体、工具名、长度、耗时、结果和 error code。

## 14. 验证

Go：

```text
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
node test_mcp.js --start-server
```

协议烟测启动 Mock LLM 和出站 Mock Runtime，验证 Agent Card、A2A
streaming Task、Runtime initialize/tool call、虚拟 MCP `tools/list`、
Agent 绑定 entityId，以及旧 `/unity/ws` 返回 404。

Unity：

- `Assembly-CSharp.csproj` 命令行编译必须 0 error。
- Editor/PlayMode 继续验证主线程、移动、取消、重连和多实体隔离。
- SampleScene 必须验证普通对话、流式文本、warehouse/gate 移动、
  Inventory、存档恢复和 Console 清洁。

## 15. 已删除内容

仓库中不再存在：

- `/unity/ws`
- `protocolVersion: 2`
- `unity.register` / `unity.npc.changed` / `unity.tools.changed`
- `unity.tool.execute` / `unity.tool.cancel`
- `conversation.start` / `player.message` / `conversation.end`
- `assistant.status` / `assistant.delta`
- `savegame.conversations.save/load`
- Go `internal/unity`
- Unity `UnityGatewayClient` / `UnityGatewayProtocol`
- v2 feature flag 或 dual-stack 路径

新架构不为旧协议提供兼容层。
