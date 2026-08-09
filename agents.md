# agents.md

## 1. 项目目标

本仓库实现 Unity + Go NPC Agent 系统：玩家在 Unity 中发起对话，Go Agent
Service 调用 LLM 并运行工具循环，Unity 在主线程执行真实游戏行为并返回结果。

| 目录 | 当前职责 |
|---|---|
| `GameMCPServer` | A2A、Conversation/LLM、MCP Adapter、Runtime Gateway、Save Coordination |
| `unity-NPC-agent-client` | UI、Agent Runtime、NPC 生命周期、工具与主线程游戏行为 |

`ARCHITECTURE.md` 是唯一架构事实源。代码、测试和文档冲突时，先核对实现，
再同步更新该文档。

## 2. 不可破坏的边界

- LLM API Key、模型请求、完整对话历史和 tool loop 只存在于 Go。
- Unity 不直接调用 LLM，不读取 LLM Key，也不维护模型历史。
- Go 不访问 Unity 对象或推断最终游戏状态。
- Unity API 只能在 Unity 主线程执行。
- 工具参数在网络边界上必须是 JSON 对象，禁止二次编码为 JSON 字符串。
- 游戏业务失败使用 `AgentToolResult` / MCP `CallToolResult.isError`；协议、
  方法或参数信封错误使用 JSON-RPC error。
- 工具 Schema 只由 Unity Runtime 生成；Go 不得硬编码第二份 Schema。
- 当前活动 Context 使用内存存储。文件归档只服务显式存档协调，不得自行扩展
  数据库、TTL、自动恢复或长期记忆。
- NPC Profile 只描述人格、职责和静态背景，不包含实时位置、库存或行为状态。

## 3. 当前协议

### A2A 对话

```text
GET  /.well-known/agent-card.json
GET  /.well-known/agent.json
POST /a2a
```

`/a2a` 使用 A2A JSON-RPC 2.0，支持：

- `message/send`
- `message/stream`（SSE）
- `tasks/cancel`

消息 metadata 必须包含 Game Context Extension：

```text
https://gamewithllm.dev/extensions/game-context/v1
```

### Runtime Bridge

唯一 Unity Runtime WebSocket：

```text
/runtime/ws
```

Unity → Gateway：

- `runtime.initialize`
- `runtime.manifest.changed`
- `runtime.progress`
- 对 `runtime.tools.call` 的 result/error

Gateway → Unity：

- `runtime.tools.call`
- `runtime.cancelled`

本地和远程使用同一协议与 `RuntimeGatewayClient`，只改变 WS/WSS 地址及凭据。

### MCP

Agent Service 内部通过 `mcp.Client` 接口调用 Registry，不做 HTTP 环回。可选
外部入口：

```text
POST /mcp/runtimes/{instanceId}
```

协议版本为 `2025-11-25`，当前实现 `initialize`、`tools/list`、
`tools/call` 和初始化/取消通知。

### Save Coordination

```text
POST /game-saves/{saveId}/agent-context:prepare
POST /game-saves/{saveId}/agent-context:commit
POST /game-saves/{saveId}/agent-context:restore
GET  /game-saves/{saveId}/agent-context:status
```

禁止重新引入：

- `/unity/ws`、`protocolVersion: 2` 或旧 `unity.*` / `conversation.*` 方法
- Unity 本地 MCP Server、本地/远程双模式或 fallback
- Unity 侧 LLM DTO、API Key 或本地模型历史
- `AGENT_HOST_*`、`UNITY_JSONRPC_WS_URL` 等旧配置

## 4. Unity SDK 边界

`Packages/com.gamewithllm.agent-runtime` 是公共契约 UPM 包。生产代码必须直接
使用：

- `IAgentEntity` / `IGameObjectAgentEntity`
- `IAgentTool` / `AgentToolDescriptor` / `AgentToolContext`
- `AgentToolResult`
- `RuntimeCommand` / `RuntimeManifest`
- `IRuntimeTransport`
- `AgentResponseEvent`

`Assets` 中不得定义平行的 Tool、Result、Command、Manifest、Entity 或
Transport 公共类型。网络客户端、Registry、Dispatcher、UI 和具体游戏逻辑
当前仍是 `Assets/Scripts` 中的生产实现，不属于契约包。

## 5. Go 代码地图

| 路径 | 职责 |
|---|---|
| `cmd/server/main.go` | HTTP 生命周期与优雅关闭 |
| `internal/config` | 环境变量和 dotenv |
| `internal/handler` | 路由装配与健康检查 |
| `internal/a2a` | Agent Card、A2A JSON-RPC/SSE、Game Context |
| `internal/agent` | Profile、Context、LLM、tool loop、对话归档 |
| `internal/mcp` | MCP 类型、Client、Entity 绑定、Runtime Adapter |
| `internal/gateway` | Runtime Bridge、Registry、generation、虚拟 MCP |
| `internal/savecoord` | 对话快照协调 |
| `internal/tools` | 模型工具目录、Schema 校验与策略 |

## 6. Unity 代码地图

| 路径 | 职责 |
|---|---|
| `Assets/Scripts/Networking/AgentHostClient.cs` | Unity 场景门面和总编排 |
| `Assets/Scripts/Networking/A2AClientAdapter.cs` | A2A JSON-RPC/SSE |
| `Assets/Scripts/Networking/RuntimeGatewayClient.cs` | `IRuntimeTransport` 实现 |
| `Assets/Scripts/Networking/SaveCoordinationClient.cs` | Save REST Client |
| `Assets/Scripts/CommandDispatcher/ToolsRegistry.cs` | Tool discovery、Schema 和 Manifest |
| `Assets/Scripts/CommandDispatcher/CommandDispatcher.cs` | 主线程路由和每实体 FIFO |
| `Assets/Scripts/CommandDispatcher/NpcTool.cs` | Warehouse `IAgentTool` 适配基类 |
| `Assets/Scripts/GameLogic/NpcEntity.cs` | Entity、NavMesh 与长时行为 |
| `Packages/com.gamewithllm.agent-runtime/Runtime` | SDK 公共契约 |

移动或重命名 Unity 资源时必须同时移动 `.meta` 并保留 GUID。

## 7. 配置

Go Agent Service：

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

优先级：进程环境变量 > `.env.local` > `.env` > 默认值。禁止提交真实密钥
和 token。

## 8. 开发规则

- 网络线程不得调用 Unity API，只能投递线程安全命令或 UI 回调。
- 所有 WebSocket 写入必须经过发送锁。
- pending 必须支持取消、断线清理、generation 和重复结果隔离。
- 新工具先在 Unity 实现 `IAgentTool`、参数契约和 Schema，再由 Runtime
  Manifest 暴露给 Go。
- 执行前重新校验 Entity、Tool、`IsAvailable`、Schema、领域参数和实时状态。
- 日志不记录玩家正文、模型全文、完整工具参数、Prompt、历史或密钥。
- 不为未来功能预建兼容分支。协议升级先更新 `ARCHITECTURE.md`，并明确旧
  路径删除条件。

## 9. 验证

Go 修改至少运行：

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
4. 取消会停止对应 Task 和移动。
5. Go 重启后 Unity 能重连并重新发布 Manifest。
6. Inventory 和存档恢复正常。
7. Console 无编译错误、Missing Script、线程或协议异常。
