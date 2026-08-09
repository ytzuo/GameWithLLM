# NPC Agent 系统

基于 Unity 与 Go 的双进程 NPC Agent 系统。玩家在 Unity 中发起对话，Go
Agent Service 调用大模型并运行工具循环，Unity 在主线程执行真实游戏行为并
返回结构化结果。

架构原则是：**Go 决策，Unity 执行**。

```text
Unity Game
├─ A2AClientAdapter ── HTTP/SSE ─────────► Go Agent Service ──► LLM
├─ SaveCoordinationClient ── HTTP/JSON ──► Save Coordinator
└─ RuntimeGatewayClient ── WebSocket ────► Runtime Gateway
                                              │
                                              └─ MCP Runtime Adapter
```

- Go 持有 LLM Key、NPC Profile、对话上下文和 tool loop。
- Unity 持有 GameObject、NavMesh、Inventory、世界存档和行为结果。
- Unity 不直接调用 LLM；Go 不直接访问 Unity 对象。
- 本地与远程 Runtime 使用同一套出站 Gateway 连接，仅地址和凭据不同。

完整协议、模块边界和交互流程以 [ARCHITECTURE.md](./ARCHITECTURE.md) 为准。

## 目录

| 路径 | 职责 |
|---|---|
| `GameMCPServer/` | Go Agent Service、A2A Server、Conversation Engine、Runtime Gateway、MCP Adapter、Save Coordinator |
| `unity-NPC-agent-client/` | Unity UI、Agent Runtime、NPC 与工具实现、世界存档 |
| `unity-NPC-agent-client/Packages/com.gamewithllm.agent-runtime/` | Unity Agent Runtime 公共契约 UPM 包 |
| `.env.example` | 本地配置模板 |

## 当前网络入口

| 入口 | 协议 | 用途 |
|---|---|---|
| `GET /.well-known/agent-card.json` | HTTP/JSON | A2A Agent Card |
| `GET /.well-known/agent.json` | HTTP/JSON | A2A Agent Card 别名 |
| `POST /a2a` | A2A JSON-RPC 2.0；流式响应为 SSE | 玩家消息、Task 和取消 |
| `GET /runtime/ws`（WebSocket Upgrade） | Runtime Bridge JSON-RPC 2.0 | Unity 注册、工具调用、结果和取消 |
| `POST /mcp/runtimes/{instanceId}` | MCP `2025-11-25` JSON-RPC 2.0 | 可选的服务端 MCP 入口 |
| `POST /game-saves/{saveId}/agent-context:prepare` | REST/JSON | 准备 Agent 对话快照 |
| `POST /game-saves/{saveId}/agent-context:commit` | REST/JSON | 确认世界与对话快照一致保存 |
| `POST /game-saves/{saveId}/agent-context:restore` | REST/JSON | 恢复对话快照 |
| `GET /game-saves/{saveId}/agent-context:status` | REST/JSON | 查询存档协调状态 |
| `GET /health` | HTTP | 健康检查 |

旧 `/unity/ws` 和 `protocolVersion: 2` 协议已经删除。

### 对话与工具调用流程

```text
玩家消息
  → A2A message/send 或 message/stream
  → Go Conversation Service / LLM tool loop
  → 进程内 mcp.Client
  → Runtime Registry
  → runtime.tools.call
  → Unity 主线程 CommandDispatcher
  → IAgentTool
  → AgentToolResult
```

A2A 当前实现 `message/send`、`message/stream` 和 `tasks/cancel`。每条
玩家消息的 metadata 必须携带 Game Context Extension：

```text
https://gamewithllm.dev/extensions/game-context/v1
```

Context 会绑定 `instanceId + playerId + agentId`，禁止在不同 Runtime、
玩家或实体之间复用。Go 会对 LLM 隐藏工具路由字段 `entityId`，
并在调用前根据当前 Context 注入，防止模型操作其他实体。

Runtime Bridge 由 Unity 主动建立连接，实现 `runtime.initialize`、
`runtime.manifest.changed`、`runtime.tools.call`、`runtime.progress` 和
`runtime.cancelled`。新连接会替换相同 `instanceId` 的旧连接，通过
generation 隔离迟到结果；断线会清理 pending，取消会传播到 Unity
的工具执行。

## 环境要求

- Go 1.26 或更高版本
- Unity `6000.3.19f1`（当前项目版本）

## 快速开始

### 1. 配置

在仓库根目录复制模板：

```powershell
Copy-Item .env.example .env.local
```

至少填写以下密钥和 token；不要提交真实值：

```env
A2A_BEARER_TOKEN=your-local-a2a-token
RUNTIME_GATEWAY_TOKEN=your-local-runtime-token
LLM_API_KEY=your-llm-key
```

如果需要使用虚拟 MCP 端点，还要填写：

```env
MCP_GATEWAY_SERVICE_TOKEN=your-local-service-token
```

常用地址默认指向同一个本地 Go 进程：

```env
AGENT_SERVICE_BASE_URL=http://127.0.0.1:8080
A2A_AGENT_URL=http://127.0.0.1:8080/a2a
RUNTIME_GATEWAY_WS_URL=ws://127.0.0.1:8080/runtime/ws
```

全部配置及默认值见 [.env.example](./.env.example)。加载优先级为：
进程环境变量 > `.env.local` > `.env` > 默认值。

配置按职责分为：

| 范围 | 配置 |
|---|---|
| Agent Service | `AGENT_SERVICE_ADDR`、`AGENT_SERVICE_BASE_URL` |
| A2A | `A2A_AGENT_URL`、`A2A_BEARER_TOKEN` |
| Unity Runtime | `RUNTIME_GATEWAY_WS_URL`、`RUNTIME_GATEWAY_TOKEN`、`UNITY_INSTANCE_ID`、`PLAYER_ID`、`UNITY_SCENE_ID` |
| 外部 MCP | `MCP_GATEWAY_SERVICE_TOKEN` |
| LLM | `LLM_API_URL`、`LLM_API_KEY`、`LLM_MODEL`、`LLM_REQUEST_TIMEOUT_SECONDS`、`LLM_MAX_RETRIES`、`LLM_MAX_TOOL_ROUNDS`、`LLM_MAX_CONTEXT_CHARS` |
| Profile 与归档 | `NPC_PROFILE_PATH`、`CONVERSATION_SAVE_DIR` |

### 2. 启动 Go Agent Service

```powershell
cd GameMCPServer
go run ./cmd/server
```

默认监听 `:8080`。可访问 `http://127.0.0.1:8080/health` 检查服务状态。

### 3. 启动 Unity

1. 使用 Unity Hub 打开 `unity-NPC-agent-client`。
2. 打开 `SampleScene`。
3. 点击 Play。

Unity 启动时会发现实体和工具，生成 `RuntimeManifest`，再主动连接
`/runtime/ws`。实体或工具变化时会发布新的完整 Manifest；Go
重启或网络中断后，Unity 会重连并重新注册。玩家消息通过
`/a2a` 发送。

## 示例能力

- 普通 NPC 对话与流式回复
- 查询 NPC 状态和可移动目标
- 使用 NavMesh 移动到 `warehouse` 或 `gate`
- 查询、放入和取出 Inventory 物品
- 协调保存和恢复 Unity 世界与 Agent 对话快照

Unity 工具命令只在主线程启动；同一实体的调用按 FIFO 串行，
不同实体可并行。执行前会重新检查 Entity、Tool、`IsAvailable`、
Schema、领域参数和实时世界状态。

工具 Schema 只由 Unity 运行时生成。新增工具时：

1. 定义继承 `ToolArgsBase` 的参数类型和约束。
2. 实现 `IAgentTool`，或继承游戏适配基类 `NpcTool<TArgs>`。
3. 使用 SDK 的 `[AgentTool]` 标记可发现工具。
4. 由 `ToolsRegistry` 反射发现、生成 Schema，并合并路由字段 `entityId`。
5. Go 从 Runtime Manifest 动态取得工具，禁止再硬编码一份 Schema。

SDK 的范围和接入方式见
[Agent Runtime README](./unity-NPC-agent-client/Packages/com.gamewithllm.agent-runtime/README.md)。

## 从旧版协议迁移

本次 SDK/Runtime 重构是破坏性升级，Go 和 Unity 需要同时更新：

| 旧实现 | 当前实现 |
|---|---|
| `/unity/ws` | `/a2a` + `/runtime/ws` |
| `protocolVersion: 2` | A2A JSON-RPC 2.0 + MCP `2025-11-25` + Runtime Bridge |
| `unity.*` / `conversation.*` | `message/*`、`tasks/cancel`、`runtime.*` |
| `UnityGatewayClient` | `A2AClientAdapter` + `RuntimeGatewayClient` + `SaveCoordinationClient` |
| `AGENT_HOST_*` | `AGENT_SERVICE_*` |
| `UNITY_JSONRPC_WS_URL` | `RUNTIME_GATEWAY_WS_URL` |
| `INpcTool` 等 Assets 内公共类型 | UPM SDK 中的 `IAgentTool`、`AgentToolResult`、`RuntimeCommand` 等 |

不提供旧协议 fallback，请不要继续使用 `AGENT_HOST_*`、
`UNITY_JSONRPC_WS_URL` 或旧 `unity.*` / `conversation.*` 方法。

## 验证

Go 测试：

```powershell
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

Unity 修改还需要完成 C# 编译，并在 `SampleScene` 验证普通对话、移动、取消、
重连、Inventory、存档恢复以及 Console 无错误。

开发边界和验证清单见 [agents.md](./agents.md)。
