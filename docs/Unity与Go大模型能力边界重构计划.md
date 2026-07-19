# Unity 与 Go 大模型能力边界重构计划

> 状态：执行中 v0.2（第一轮 Go M0 + M1 已完成）  
> 更新日期：2026-07-19  
> 适用仓库：GameWithLLM  
> 关联文档：[GameMCPServer 改造与目标架构建议](./GameMCPServer改造与目标架构建议.md)

## 0. 执行状态

### 0.1 第一轮结果（2026-07-19）

第一轮聚焦 Go Server 的测试保护和 WebSocket 基础设施，不迁移 LLM 会话，也不改变 `tools/list` / `tools/call` 的业务消息结构。

已完成：

- 建立 Go JSON-RPC Session、路由和真实 WebSocket 集成测试。
- 修复未知工具被转发后只能等待超时的问题，现在立即返回 `-32601`。
- 修复重复响应可能阻塞会话读循环的问题。
- 使用 `github.com/coder/websocket` 替换手写握手、帧、分片和 Ping/Pong 处理。
- 删除 `GameMCPServer/internal/unity/websocket.go`。
- 设置 1 MiB 单消息读取上限，并验证 Ping/Pong、分片消息和 16 KiB 消息。
- 新增正式入口 `/unity/ws`；`/ws` 和根路径暂时兼容并输出弃用日志。
- 使用显式 `http.Server`，增加 Header/Idle 超时、Signal 监听、HTTP Shutdown 和 WebSocket `Going Away` 关闭。
- 将 Go、Unity、示例环境变量和启动文档的默认地址切换到 `/unity/ws`。
- 将 `test_mcp.js` 的手写 WebSocket 客户端替换为 Node.js 标准 WebSocket。

验证结果：

```text
go test ./...        PASS
go test -race ./...  PASS
端到端协议测试       13 PASS / 0 FAIL
```

端到端测试在 `18080` 端口执行，因为当前开发环境的 `8080` 已被其他进程占用；未终止或修改该进程。

本轮基线：

| 项目 | 值 |
|---|---|
| 开始 Commit | `c639f021936caf908c69df283c1ec63359c7b94c` |
| Go | `go1.26.4 darwin/arm64` |
| Unity | `6000.3.19f1` |

未纳入第一轮：

- Unity 协议 DTO 自动化测试。
- 内部执行协议 v1 和 Unity Registry。
- Unity 客户端职责拆分。
- LLM 调用与历史迁移到 Go。

## 1. 计划目标

本计划用于把当前的 Unity → Go → Unity 工具调用环回链路，逐步重构为职责明确、可测试、可扩展的 Agent Host 架构。

最终能力边界：

- Go 负责理解、记忆、决策、工具策略、模型调用和请求追踪。
- Unity 负责玩家交互、游戏世界状态、NPC 状态机和实际行为执行。
- Go 与 Unity 使用独立的内部执行协议通信。
- 标准 MCP 作为可选的外部接口，由官方 SDK 实现，不与 Unity 内部协议混用。

本次重构必须保持以下现有能力：

- 玩家可以与指定 NPC 开始会话。
- 模型可以发起 `game_npc_move`。
- 工具命令通过 NPC ID 路由到正确实体。
- Unity API 只在主线程执行。
- 工具结果能够回到模型上下文并产生最终回复。

---

## 2. 当前基线

### 2.1 当前链路

```mermaid
sequenceDiagram
    participant UI as Unity UI
    participant UC as McpAsyncClient
    participant LLM as LLM API
    participant Go as GameMCPServer
    participant NPC as NpcEntity

    UI->>UC: 玩家输入
    UC->>LLM: messages + tools
    LLM-->>UC: tool_call
    UC->>Go: tools/call
    Go->>UC: 转发 tools/call
    UC->>NPC: CommandDispatcher 入队
    NPC-->>UC: 执行结果
    UC->>Go: result
    Go-->>UC: result
    UC->>LLM: tool result
    LLM-->>UC: 最终回复
```

### 2.2 当前主要问题

- Go 仅做环回转发，没有形成安全或会话边界。
- Unity 保存 LLM API Key 并直接调用模型。
- `McpAsyncClient.cs` 同时承担网络、会话、模型和工具调度职责。
- Go 手写 WebSocket 握手与帧协议。
- Go 与 Unity 重复维护工具 Schema。
- 内部协议使用 MCP 方法名，但没有实现完整 MCP 生命周期。
- Unity WebSocket 接收没有正确拼接分片消息。
- pending 请求缺少统一的线程安全、取消和断线清理。
- Go 包当前没有单元测试。

### 2.3 重构前基线确认

开始代码改造前必须记录：

- 当前 `main` Commit SHA。
- Unity Editor 版本。
- Go 版本。
- 当前可运行场景和启动步骤。
- 一次完整对话和工具调用日志。
- 当前 `.env` 所需配置项。

基线验收命令：

```bash
cd GameMCPServer
go test ./...
```

Unity 侧需要人工确认：

1. 打开 `SampleScene`。
2. 启动 Go 服务。
3. 进入 Play Mode。
4. 打开聊天窗口。
5. 发送一条能触发移动工具的消息。
6. 记录当前成功和失败行为。

---

## 3. 目标架构

```mermaid
flowchart LR
    UI["Unity UI / ChatViewModel"] -->|"player.message"| Host["Go Agent Host"]
    Host --> Session["Conversation Session"]
    Session --> LLM["LLM Provider"]
    LLM -->|"tool call"| Policy["Tool Catalog / Policy"]
    Policy --> Executor["ToolExecutor"]
    Executor -->|"unity.tool.execute"| Gateway["Unity Gateway Client"]
    Gateway --> Dispatcher["CommandDispatcher"]
    Dispatcher --> NPC["NpcEntity / FSM"]
    NPC -->|"tool result"| Executor
    Session -->|"assistant.message"| UI
    MCP["External MCP Client（可选）"] -.->|"/mcp"| Host
```

### 3.1 Go 的目标职责

- LLM Provider 配置和 API Key。
- NPC 对话 Session。
- System Prompt 和消息历史。
- 模型请求、重试和错误处理。
- Tool-call 循环。
- 模型可见工具列表。
- JSON Schema 和权限校验。
- NPC Session 绑定。
- 请求 ID、超时、取消和审计。
- Unity 实例、NPC 和工具能力注册。
- 可选标准 MCP 接口。

### 3.2 Unity 的目标职责

- 玩家输入和聊天窗口展示。
- 当前 Unity 实例注册。
- NPC 上下线与运行时能力注册。
- 接收工具执行命令。
- 网络线程到主线程的安全投递。
- NPC FSM 和实时状态校验。
- NavMesh、动画和 GameObject 操作。
- 返回工具进度和最终结果。

### 3.3 双层校验边界

Go 校验：

- 工具是否存在。
- 参数是否符合 Schema。
- Session 是否绑定 NPC。
- NPC 是否已注册并在线。
- 当前调用方是否有权限。
- 请求是否超时或被取消。
- 是否超过并发限制。

Unity 校验：

- NPC GameObject 是否仍存在。
- NPC 当前 FSM 是否允许执行。
- 地标、路径和游戏资源是否存在。
- NavMesh 是否可达。
- 动作是否执行成功。
- 游戏状态是否发生变化。

Unity 是游戏世界状态的最终权威。

---

## 4. 总体实施策略

重构采用渐进迁移，不在一个提交中同时替换网络、协议、LLM 和 UI。

原则：

1. 先建立测试基线。
2. 先替换底层 WebSocket，不改变业务行为。
3. 再规范 Go ↔ Unity 内部协议。
4. 再拆分 Unity 客户端职责。
5. 最后把 LLM 会话迁到 Go。
6. 标准 MCP 接口作为独立的可选阶段。

每个阶段必须：

- 可以独立构建和验证。
- 有明确回滚点。
- 不依赖未完成的下一阶段。
- 使用独立提交或 PR。
- 完成验收后再进入下一阶段。

---

## 5. 里程碑总览

| 里程碑 | 目标 | 主要产物 | 依赖 |
|---|---|---|---|
| M0 | 建立基线与测试保护 | 协议测试、并发测试、运行记录 | 无 |
| M1 | 替换手写 WebSocket | `coder/websocket`、优雅关闭 | M0 |
| M2 | 建立内部协议 v1 | 注册、执行、结果、错误模型 | M1 |
| M3 | 拆分 Unity 网络职责 | `UnityGatewayClient`、可靠收发 | M2 |
| M4 | 把 Agent Host 迁到 Go | LLM Client、Session、tool loop | M3 |
| M5 | 收敛历史与工具契约 | 单一 Schema 来源、持久化接口 | M4 |
| M6 | 可选标准 MCP 接口 | 官方 Go MCP SDK、`/mcp` | M5 |

---

## 6. M0：基线与测试保护

### 6.1 目标

在改变协议前，建立能够捕获回归的自动化测试。

### 6.2 任务

#### M0-GO-01：为 JSON-RPC Session 增加单元测试

覆盖：

- `tools/list` 正常响应。
- `tools/call` 正常转发。
- 重复请求 ID。
- 缺少请求 ID。
- 缺少 `npcId`。
- 缺少工具名。
- 缺少参数。
- Unity 返回成功。
- Unity 返回错误。
- Unity 执行超时。
- 连接断开。

建议新增：

```text
GameMCPServer/internal/unity/session_test.go
GameMCPServer/internal/unity/protocol_test.go
```

#### M0-GO-02：替换手写 JavaScript WebSocket 测试客户端

当前 `test_mcp.js` 自行实现了 WebSocket 帧协议。M1 完成后应改为使用成熟客户端；M0 阶段先保留它作为兼容性基线，但不继续扩展其底层帧代码。

#### M0-GO-03：增加 Race Test

```bash
go test -race ./...
```

重点验证：

- `pending` map。
- 并发工具调用。
- 连接断开和超时同时发生。
- 多个 goroutine 写同一连接。

#### M0-UNITY-01：建立协议 DTO 测试

覆盖：

- 请求反序列化。
- 成功结果序列化。
- 错误结果序列化。
- 大于 8192 字节消息。
- 无效参数。

### 6.3 验收标准

- Go 的核心协议路径有自动化测试。
- `go test ./...` 通过。
- `go test -race ./...` 通过。
- 当前端到端行为被记录并可重复。

### 6.4 回滚点

该阶段只增加测试，不修改生产行为，可直接回滚测试提交。

---

## 7. M1：替换手写 WebSocket

### 7.1 目标

在不改变业务消息结构的情况下，用成熟库替换 WebSocket 握手和帧处理。

### 7.2 技术选型

使用：

```text
github.com/coder/websocket
```

继续使用：

```text
net/http
encoding/json
```

### 7.3 Go 任务

#### M1-GO-01：引入 WebSocket 库

修改：

```text
GameMCPServer/go.mod
GameMCPServer/go.sum
```

#### M1-GO-02：删除手写帧协议

删除：

```text
GameMCPServer/internal/unity/websocket.go
```

由库负责：

- Upgrade。
- 帧和分片。
- Ping/Pong。
- Close handshake。
- Context 取消。
- JSON 消息边界。

#### M1-GO-03：重写连接接入

调整：

```text
GameMCPServer/internal/unity/server.go
GameMCPServer/internal/unity/session.go
```

约束：

- 每个 Session 有独立 Context。
- 消息大小上限默认 1 MiB。
- 所有写操作串行化。
- 正常关闭使用标准 Close Code。
- 连接断开立即取消全部 pending 请求。

#### M1-GO-04：规范路由

将 WebSocket 入口统一为：

```text
/unity/ws
```

兼容期内保留 `/ws` 和根路径 WebSocket Upgrade，但输出弃用日志；待 Unity 默认配置完成迁移并经过一轮发布验证后删除根路径兼容。

#### M1-GO-05：HTTP Server 生命周期

把 `http.ListenAndServe` 改为显式 `http.Server`：

- `ReadHeaderTimeout`。
- `IdleTimeout`。
- Signal 监听。
- `Shutdown(ctx)`。

### 7.4 Unity 任务

该阶段不改变 Unity 消息格式，只调整默认地址为 `/unity/ws`；兼容期也可以继续连接 `/ws`。

### 7.5 验收标准

- 现有工具调用闭环保持不变。
- 手写 WebSocket 代码完全删除。
- 连接关闭后没有遗留 goroutine。
- 大于单帧缓冲区的消息可以被 Go 正确读取。
- `/health` 不受影响。

### 7.6 回滚点

保留 M0 测试基线。如库替换失败，整体回滚 M1 提交，不影响消息协议。

---

## 8. M2：内部执行协议 v1

### 8.1 目标

把 Go ↔ Unity 通道明确为版本化的私有执行协议，取消“半标准 MCP”语义。

### 8.2 协议方法

Unity → Go：

- `unity.register`
- `unity.npc.changed`
- `unity.tools.changed`
- 工具调用响应
- `unity.tool.progress`（可选）

Go → Unity：

- `unity.tool.execute`
- `unity.tool.cancel`
- Ping/状态请求（按需）

### 8.3 注册消息

```json
{
  "jsonrpc": "2.0",
  "id": "register-1",
  "method": "unity.register",
  "params": {
    "protocolVersion": 1,
    "instanceId": "local-game-1",
    "tools": [],
    "npcs": ["Ryan_001"]
  }
}
```

### 8.4 工具执行消息

```json
{
  "jsonrpc": "2.0",
  "id": "call-123",
  "method": "unity.tool.execute",
  "params": {
    "npcId": "Ryan_001",
    "tool": "game_npc_move",
    "arguments": {
      "targetLandmark": "warehouse"
    }
  }
}
```

### 8.5 结果模型

成功：

```json
{
  "jsonrpc": "2.0",
  "id": "call-123",
  "result": {
    "ok": true,
    "message": "NPC开始移动"
  }
}
```

业务失败：

```json
{
  "jsonrpc": "2.0",
  "id": "call-123",
  "result": {
    "ok": false,
    "errorCode": "LANDMARK_NOT_FOUND",
    "message": "目标地标不存在"
  }
}
```

只有协议解析或参数格式错误使用 JSON-RPC `error`。

### 8.6 Go 任务

#### M2-GO-01：建立强类型协议 DTO

修改：

```text
GameMCPServer/internal/unity/protocol.go
```

要求：

- `arguments` 是 JSON 对象，不再是二次编码字符串。
- 明确 `protocolVersion`。
- 明确业务错误码。
- 不在内部协议使用标准 MCP ToolResult 类型。

#### M2-GO-02：实现 Unity Registry

新增：

```text
GameMCPServer/internal/unity/registry.go
```

负责：

- `instanceID -> Session`。
- `npcID -> instanceID`。
- 重复注册策略。
- 断线清理。
- NPC 上下线。
- 工具目录更新。

#### M2-GO-03：实现 ToolExecutor

新增：

```text
GameMCPServer/internal/unity/tool_executor.go
```

接口：

```go
type ToolExecutor interface {
    Execute(
        ctx context.Context,
        instanceID string,
        npcID string,
        tool string,
        arguments json.RawMessage,
    ) (*ToolResult, error)
}
```

#### M2-GO-04：移除硬编码工具表

逐步删除：

```text
GameMCPServer/internal/unity/tools.go
```

Go 的工具目录来自 Unity 注册消息。

### 8.7 Unity 任务

- 连接成功后发送 `unity.register`。
- NPC 注册和注销时同步变化。
- 工具发生变化时同步目录。
- 接收 `unity.tool.execute`。
- 返回统一结果模型。

### 8.8 兼容策略

协议 v1 迁移期间：

- Go 同时接受旧 `tools/call` 和新 `unity.tool.execute`。
- Unity 优先使用 v1。
- 日志标记旧协议调用。
- v1 稳定后删除旧协议。

### 8.9 验收标准

- Unity 注册成功前，Go 不发送工具命令。
- 未注册 NPC 在 Go 侧立即失败。
- 未注册工具在 Go 侧立即失败。
- `arguments` 不再出现 JSON 字符串二次编码。
- Unity 重连后注册状态可恢复。

---

## 9. M3：拆分 Unity 网络职责

### 9.1 目标

把网络连接从 LLM 会话逻辑中拆出，建立可靠的 Unity Gateway Client。

### 9.2 新增文件

```text
unity-NPC-agent-client/Assets/Scripts/Networking/UnityGatewayClient.cs
unity-NPC-agent-client/Assets/Scripts/Networking/UnityGatewayProtocol.cs
unity-NPC-agent-client/Assets/Scripts/Networking/ReconnectPolicy.cs
```

### 9.3 UnityGatewayClient 职责

- 建立和关闭 WebSocket。
- 发送实例注册信息。
- 接收完整消息。
- 协议反序列化。
- pending 请求管理。
- 串行发送。
- 超时和取消。
- 断线清理。
- 指数退避重连。
- 重连后重新注册。

### 9.4 必须修复的网络问题

#### M3-UNITY-01：完整读取 WebSocket 消息

循环调用 `ReceiveAsync`，直到 `EndOfMessage=true`，再解析 JSON。

#### M3-UNITY-02：发送串行化

使用 `SemaphoreSlim` 或单发送队列，保证同一 `ClientWebSocket` 不发生并发 Send。

#### M3-UNITY-03：线程安全 pending

使用 `ConcurrentDictionary` 或统一网络线程管理 pending。

#### M3-UNITY-04：统一取消

连接关闭时：

- 取消接收循环。
- 使所有 pending 请求失败。
- 释放 WebSocket。
- 进入重连策略。

#### M3-UNITY-05：移除换行帧

WebSocket 消息不再追加 `\n`。

### 9.5 ChatViewModel 调整

该阶段 `ChatViewModel` 仍可调用旧的 LLM 会话入口，但不再感知 WebSocket 细节。

### 9.6 McpAsyncClient 过渡

将 `McpAsyncClient` 暂时缩减为：

- 旧 LLM 会话编排。
- 调用 `UnityGatewayClient`。

M4 完成后再删除其中的 LLM 能力。

### 9.7 验收标准

- 大于 8192 字节的消息可正确读取。
- 多个并发发送不会抛出 WebSocket 状态异常。
- Go 重启后 Unity 可以自动重连。
- 断线时所有等待请求有明确错误。
- Unity 主线程不执行网络 I/O。

---

## 10. M4：将 Agent Host 迁移到 Go

### 10.1 目标

把 LLM API Key、对话历史、模型请求和 tool-call 循环从 Unity 迁移到 Go。

### 10.2 Go 新增模块

```text
GameMCPServer/internal/agent/conversation.go
GameMCPServer/internal/agent/session.go
GameMCPServer/internal/agent/session_store.go
GameMCPServer/internal/agent/llm_client.go
GameMCPServer/internal/agent/messages.go
GameMCPServer/internal/tools/catalog.go
GameMCPServer/internal/tools/validator.go
GameMCPServer/internal/tools/policy.go
```

### 10.3 核心接口

#### LLM Provider

```go
type LLMClient interface {
    Complete(ctx context.Context, req CompletionRequest) (*CompletionResult, error)
}
```

业务层不直接依赖某个厂商的 HTTP DTO。

#### Conversation Service

```go
type ConversationService interface {
    StartSession(ctx context.Context, playerID, npcID string) (*Session, error)
    SubmitMessage(ctx context.Context, sessionID, text string) (*AssistantReply, error)
    EndSession(ctx context.Context, sessionID string) error
}
```

### 10.4 Session 数据

每个 Session 至少包含：

- `sessionID`。
- `playerID`。
- `npcID`。
- `unityInstanceID`。
- System Prompt。
- 消息历史。
- 当前模型配置。
- 当前进行中的 tool call。
- 创建和最后活跃时间。
- Session 级取消函数。

### 10.5 新对话链路

Unity → Go：

```json
{
  "type": "player.message",
  "sessionId": "session-123",
  "text": "去仓库看看"
}
```

Go → Unity：

```json
{
  "type": "assistant.message",
  "sessionId": "session-123",
  "npcId": "Ryan_001",
  "text": "我去仓库入口确认一下。"
}
```

### 10.6 Go Tool-call 循环

1. 接收玩家消息。
2. 加载 Session 历史。
3. 根据 NPC 和权限生成模型可见工具。
4. 调用 LLM。
5. 如果返回 tool call：
   - 校验工具与参数。
   - 从 Session 注入 `npcId`。
   - 调用 `ToolExecutor`。
   - 将结果加入消息历史。
   - 再次调用 LLM。
6. 返回最终文本。
7. 保存完整原子历史。

### 10.7 Unity 删除能力

从 `McpAsyncClient.cs` 迁移或删除：

- `llmApiUrl`。
- `llmModel`。
- `llmApiKey`。
- `HttpClient`。
- `SendLlmRequestAsync`。
- `SessionTaskAsync` 中的模型循环。
- Unity 侧 LLM messages 权威历史。
- `ToolsRegistry.GetToolsForLlm()`。

### 10.8 Unity 保留能力

- `ChatViewModel`。
- 玩家消息即时展示。
- 发送 `player.message`。
- 接收 `assistant.message`。
- 展示思考、工具执行和错误状态。
- 可选的本地 UI 缓存。

### 10.9 配置迁移

Go 新增：

- `LLM_API_URL`。
- `LLM_API_KEY` 或继续兼容 `OPENAI_API_KEY`。
- `LLM_MODEL`。
- `LLM_REQUEST_TIMEOUT_SECONDS`。
- `LLM_MAX_TOOL_ROUNDS`。

Unity 删除对 API Key 的读取。

### 10.10 功能开关

迁移期建议支持：

```text
AGENT_HOST_MODE=unity|go
```

- `unity`：保留旧会话链路，用于回滚。
- `go`：使用新 Agent Host。

稳定后删除 `unity` 模式。

### 10.11 验收标准

- Unity 构建物中不包含 LLM API Key。
- Unity 不再直接请求 LLM 服务。
- Go 能完成无工具和有工具两种对话。
- 工具调用最多执行配置的轮数，防止无限循环。
- 每个 NPC Session 相互隔离。
- Go 重启后的 Session 行为符合明确策略。
- 工具请求与结果在历史中保持原子配对。

---

## 11. M5：历史、工具契约与状态收敛

### 11.1 目标

确定单一数据来源，消除 Go 与 Unity 的重复状态。

### 11.2 工具 Schema 所有权

采用两阶段所有权：

1. Unity 是“实际可执行工具”的来源。
2. Go 是“模型可见工具”的来源。

Unity 注册运行时能力；Go 根据 Session、NPC、权限和游戏模式筛选后交给 LLM。

### 11.3 对话历史所有权

Go 是权威来源，Unity 只保存展示缓存。

定义存储接口：

```go
type SessionStore interface {
    Load(ctx context.Context, sessionID string) (*Session, error)
    Save(ctx context.Context, session *Session) error
    Delete(ctx context.Context, sessionID string) error
}
```

第一版可以使用内存实现，后续再决定文件或数据库。

### 11.4 NPC 状态所有权

- Unity 持有实时状态。
- Go 只持有用于路由和策略的状态快照。
- Go 不直接推断 Unity GameObject 是否存在。
- 执行前后的最终判断由 Unity 返回。

### 11.5 验收标准

- 工具 Schema 不再手动复制两份。
- Go 可根据 NPC 筛选模型工具。
- Unity 重连后 Go 的能力目录正确更新。
- Unity 清空 UI 不会删除 Go 权威历史。

---

## 12. M6：可选标准 MCP 接口

### 12.1 启动条件

只有明确需要以下能力时实施：

- Codex、Claude Desktop 等标准 MCP Client 控制游戏。
- 外部 Agent 调用游戏工具。
- 将游戏能力暴露给调试或自动化平台。

### 12.2 技术选型

使用：

```text
github.com/modelcontextprotocol/go-sdk/mcp
```

传输：

```text
Streamable HTTP /mcp
```

### 12.3 约束

- MCP Handler 只能调用统一 `ToolExecutor`。
- MCP 层不直接访问 WebSocket Session 内部字段。
- Unity 私有 `instanceId` 和内部错误不得直接泄漏。
- 必须定义外部请求如何绑定玩家和 NPC。
- 远程访问必须增加认证、授权、限流和审计。

### 12.4 验收标准

- 标准客户端可完成 initialize、tools/list 和 tools/call。
- 外部 MCP 和游戏内部对话复用相同工具执行路径。
- 未授权客户端不能调用工具。
- Unity 断线时 MCP 调用得到明确错误。

---

## 13. 文件级改造清单

### 13.1 Go

| 当前文件 | 动作 | 目标 |
|---|---|---|
| `cmd/server/main.go` | 修改 | HTTP Server 超时和优雅关闭 |
| `internal/handler/router.go` | 修改 | 增加 `/unity/ws`，逐步弃用旧入口 |
| `internal/unity/websocket.go` | 删除 | 使用 `coder/websocket` |
| `internal/unity/server.go` | 重写 | Gateway 连接接入 |
| `internal/unity/session.go` | 拆分 | Session、pending、读写循环 |
| `internal/unity/protocol.go` | 重写 | 内部协议 v1 DTO |
| `internal/unity/tools.go` | 删除/迁移 | 工具来自 Unity 注册 |
| `internal/unity/registry.go` | 新增 | Unity/NPC 注册表 |
| `internal/unity/pending.go` | 新增 | 请求关联和取消 |
| `internal/unity/tool_executor.go` | 新增 | 统一工具执行接口 |
| `internal/agent/*` | 新增 | LLM 和会话编排 |
| `internal/tools/*` | 新增 | 工具目录、Schema、Policy |
| `internal/mcp/server.go` | 可选新增 | 标准 MCP 接口 |

### 13.2 Unity

| 当前文件 | 动作 | 目标 |
|---|---|---|
| `McpAsyncClient.cs` | 拆分并最终删除/重命名 | 移除混合职责 |
| `Networking/UnityGatewayClient.cs` | 新增 | 可靠 WebSocket 客户端 |
| `Networking/UnityGatewayProtocol.cs` | 新增 | 强类型内部协议 |
| `Networking/ReconnectPolicy.cs` | 新增 | 自动重连 |
| `ToolsRegistry.cs` | 修改 | 注册实际执行能力，不再直接服务 LLM |
| `CommandDispatcher.cs` | 小幅修改 | 接收新协议 DTO |
| `McpToolWrapper.cs` | 保留并增强 | Unity 最终状态校验 |
| `NpcEntity.cs` | 修改 | 标准工具结果和错误码 |
| `ChatViewModel.cs` | 修改 | 发送玩家消息、接收 Go 回复 |
| `FileHistoryProvider.cs` | 降级/迁移 | UI 缓存或移除权威职责 |

---

## 14. 测试矩阵

| 场景 | Go 单测 | Go 集成 | Unity 测试 | 人工 E2E |
|---|---:|---:|---:|---:|
| Unity 注册 | ✓ | ✓ | ✓ | ✓ |
| NPC 上下线 | ✓ | ✓ | ✓ | ✓ |
| 工具发现 | ✓ | ✓ | ✓ | ✓ |
| 正常移动 | ✓ | ✓ | ✓ | ✓ |
| 未知工具 | ✓ | ✓ | ✓ | ✓ |
| 未知 NPC | ✓ | ✓ | ✓ | ✓ |
| 无效参数 | ✓ | ✓ | ✓ | ✓ |
| 地标不存在 | ✓ | ✓ | ✓ | ✓ |
| 工具超时 | ✓ | ✓ | ✓ | ✓ |
| 连接断开 | ✓ | ✓ | ✓ | ✓ |
| 自动重连 |  | ✓ | ✓ | ✓ |
| 并发工具调用 | ✓ | ✓ | ✓ | ✓ |
| 大消息/分片 |  | ✓ | ✓ | ✓ |
| 无工具对话 | ✓ | ✓ | ✓ | ✓ |
| 多轮工具对话 | ✓ | ✓ | ✓ | ✓ |
| 多 NPC Session 隔离 | ✓ | ✓ | ✓ | ✓ |
| 服务优雅关闭 |  | ✓ |  | ✓ |

---

## 15. 可观测性要求

每次对话和工具调用至少记录：

- `sessionId`。
- `playerId`（如果存在）。
- `npcId`。
- `unityInstanceId`。
- `requestId` / `toolCallId`。
- 工具名称。
- 执行时长。
- 成功、业务失败或协议失败。
- LLM 请求耗时。
- Tool-call 轮数。

日志不得记录：

- LLM API Key。
- 用户 Secret。
- 未经处理的敏感配置。

建议指标：

- Unity 在线连接数。
- 在线 NPC 数。
- 活跃 Session 数。
- LLM 请求成功率和延迟。
- 工具调用成功率和延迟。
- 工具超时次数。
- Unity 重连次数。
- pending 请求数量。

---

## 16. 风险与缓解措施

### 风险 1：一次性迁移范围过大

缓解：严格按 M0 → M6 分阶段，每阶段保留可运行状态。

### 风险 2：Unity 与 Go 协议不同步

缓解：协议带版本；迁移期双协议兼容；增加双方 DTO 测试和集成测试。

### 风险 3：LLM 迁移后对话行为变化

缓解：保存当前请求样本；Go 使用相同 model、prompt、tools 和消息顺序做对比测试。

### 风险 4：工具调用无限循环

缓解：配置 `LLM_MAX_TOOL_ROUNDS`，超限后返回可诊断错误。

### 风险 5：NPC 状态与 Go 缓存不一致

缓解：Unity 保持最终权威；Go 缓存只用于预检查；Unity 返回明确业务错误。

### 风险 6：断线导致永久等待

缓解：所有 pending 请求绑定连接 Context；断线统一失败；每次调用有 deadline。

### 风险 7：历史迁移丢失

缓解：先定义 `SessionStore`；迁移期保留 Unity 历史只读备份；完成一致性检查后再删除旧写入。

### 风险 8：本机其他进程调用游戏工具

缓解：只监听 `127.0.0.1`；增加随机 Session Token；限制消息大小和速率。

---

## 17. 提交与评审策略

建议提交边界：

1. `test: add gateway protocol baseline tests`
2. `refactor: replace raw websocket implementation`
3. `feat: add unity gateway protocol v1`
4. `refactor: split unity gateway client from mcp async client`
5. `feat: add go conversation session and llm client`
6. `refactor: route chat messages through go agent host`
7. `refactor: unify runtime tool catalog`
8. `feat: add standard mcp endpoint`（可选）

每个评审必须提供：

- 改动前后时序。
- 新增或更新的测试。
- 手工验证步骤。
- 配置变化。
- 回滚方法。
- 已知限制。

禁止在同一个提交中同时进行：

- WebSocket 库替换与 LLM 迁移。
- 协议字段重命名与 UI 大改。
- 历史存储迁移与工具 Schema 重构。

---

## 18. 待确认决策

以下决策在对应阶段开始前确认，不阻塞 M0 和 M1：

| 决策 | 推荐默认值 | 最晚确认阶段 |
|---|---|---|
| LLM Provider | 先兼容当前 OpenAI 请求格式 | M4 |
| 对话历史存储 | 第一版内存，接口预留持久化 | M4 |
| 玩家身份模型 | 单机默认 `local-player` | M4 |
| Unity 实例 ID | 配置值 + 启动实例 UUID | M2 |
| NPC 并发策略 | 同一 NPC 同时一个行为工具 | M4 |
| 工具进度通知 | 第一版只返回最终结果 | M2 |
| 外部 MCP | 默认不实现 | M6 |
| 离线降级 | 第一版返回不可用，不在 Unity 调 LLM | M4 |

---

## 19. Definition of Done

整个重构完成需同时满足：

- Go 不再手写 WebSocket 帧协议。
- Go ↔ Unity 使用版本化内部协议。
- Unity 可以注册实例、NPC 和工具能力。
- 工具参数不再二次编码为 JSON 字符串。
- Go 管理 LLM API Key、模型调用和对话历史。
- Unity 不直接访问 LLM API。
- Unity 只在主线程执行游戏对象操作。
- 工具 Schema 有明确单一来源。
- 所有工具调用有超时、取消和断线清理。
- Go 单元测试和 Race Test 通过。
- Unity 协议和主线程路由测试通过。
- 完整对话、工具调用和最终回复 E2E 通过。
- 文档、启动说明和配置示例同步更新。

---

## 20. 第一轮执行清单

第一轮只启动 M0 和 M1，不同时迁移 LLM。

- [ ] 建立重构分支。
- [x] 记录当前自动化端到端运行日志。
- [x] 为 `jsonRPCSession` 增加测试。
- [x] 增加并发和超时测试。
- [x] 引入 `coder/websocket`。
- [x] 删除 `internal/unity/websocket.go`。
- [x] 保持旧消息格式通过全部测试。
- [x] 增加 `/unity/ws` 并兼容 `/ws`。
- [x] 增加 Go HTTP Server 优雅关闭。
- [x] 运行 `go test ./...`。
- [x] 运行 `go test -race ./...`。
- [ ] 使用 Unity 完成一次人工工具调用验证。
- [x] 更新启动说明中的 WebSocket 地址。

M0、M1 验收完成后，再评审 M2 内部协议 v1 的字段和兼容窗口。
