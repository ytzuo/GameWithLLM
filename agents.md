# agents.md

## 1. 项目目标

本仓库实现一个 Unity + Go 的 NPC Agent 系统：玩家在 Unity 中发起对话，Go Agent Host 调用大模型并决定是否执行工具，Unity 在主线程执行真实游戏行为并返回结果。

代码目录：

| 目录 | 当前职责 |
|---|---|
| `GameMCPServer` | Go Agent Host：LLM、Session、工具策略、参数校验、Unity 注册与命令调度 |
| `unity-NPC-agent-client` | Unity 执行端：UI、NPC 生命周期、运行时能力声明、主线程游戏行为 |

`ARCHITECTURE.md` 是架构事实源。代码与文档冲突时，应先核对实现和测试，再同步更新该文档。

## 2. 不可破坏的边界

- LLM API Key、模型请求、对话历史和 tool loop 只存在于 Go。
- Unity 不得直接请求 LLM，也不得读取任何 LLM Key。
- Go 不直接访问 Unity 对象或推断 GameObject 的最终状态。
- Unity API 只能在 Unity 主线程执行。
- 工具参数在网络协议中必须是 JSON 对象，禁止二次编码为 JSON 字符串。
- 游戏业务失败使用 `{ok,errorCode,message}`；JSON-RPC 信封或方法错误使用 JSON-RPC error。
- 工具 Schema 的实际能力来源只有 Unity 运行时注册；Go 根据 Session/NPC/策略筛选后暴露给模型。
- 当前对话存储是内存实现。不得自行增加数据库、TTL、长期记忆或恢复机制。

## 3. 唯一有效协议

唯一 Unity WebSocket 入口：

```text
/unity/ws
```

Unity 发往 Go：

- `unity.register`
- `unity.npc.changed`
- `unity.tools.changed`
- `conversation.start`
- `player.message`
- `conversation.end`

Go 发往 Unity：

- `unity.tool.execute`
- `unity.tool.cancel`
- `assistant.status`
- `assistant.delta`

禁止重新引入：

- `/ws` 或根路径 WebSocket Upgrade
- `tools/list` / `tools/call` 环回协议
- `protocol=legacy`
- Unity 侧 LLM DTO、HttpClient、API Key 或本地对话历史
- `OPENAI_API_KEY`、`MCP_SERVER_ADDR`、`MCP_BASE_URL` 等旧配置名称

`protocolVersion: 1` 是当前内部协议版本，不代表兼容旧实现。服务端只接受当前版本。

## 4. Go 代码地图

| 路径 | 职责 |
|---|---|
| `cmd/server/main.go` | 进程入口、Signal、HTTP 生命周期与优雅关闭 |
| `internal/config/config.go` | 环境变量和根目录 dotenv 配置 |
| `internal/handler/router.go` | `/unity/ws`、`/health` 和精确根路径路由 |
| `internal/agent/llm_client.go` | OpenAI-compatible Chat Completions 客户端 |
| `internal/agent/conversation.go` | 对话编排、tool loop、最大轮数 |
| `internal/agent/session*.go` | Session 模型与内存存储 |
| `internal/tools/catalog.go` | 模型可见工具结构 |
| `internal/tools/validator.go` | JSON Schema 参数校验 |
| `internal/tools/policy.go` | 工具调用策略 |
| `internal/unity/protocol.go` | 内部 JSON-RPC DTO |
| `internal/unity/registry.go` | Unity 实例、NPC 和能力快照 |
| `internal/unity/tool_executor.go` | Unity 工具执行、超时和错误映射 |
| `internal/unity/session.go` | 单连接读循环、pending、对话与执行路由 |
| `internal/unity/server.go` | WebSocket 接入、连接追踪和关闭 |

## 5. Unity 代码地图

| 路径 | 职责 |
|---|---|
| `Assets/Scripts/Networking/AgentHostClient.cs` | Unity 场景门面、会话创建、玩家消息与 UI 回调 |
| `Assets/Scripts/Networking/UnityGatewayClient.cs` | WebSocket、注册、重连、pending、协议收发 |
| `Assets/Scripts/Networking/UnityGatewayProtocol.cs` | 当前协议 DTO 和方法常量 |
| `Assets/Scripts/CommandDispatcher/ToolsRegistry.cs` | 运行时工具 Schema 注册与快照 |
| `Assets/Scripts/CommandDispatcher/CommandDispatcher.cs` | 网络命令到 NPC 的主线程路由 |
| `Assets/Scripts/CommandDispatcher/GameToolWrapper.cs` | 参数反序列化、Validate 和异常隔离 |
| `Assets/Scripts/Models/ToolArgsBase.cs` | 工具参数与稳定执行结果类型 |
| `Assets/Scripts/Models/Models.cs` | `UnityToolCommand` DTO |
| `Assets/Scripts/GameLogic/NpcEntity.cs` | NPC 队列、NavMesh 行为与结果回传 |
| `Assets/Scripts/UIManager/*` | 对话框展示与输入体验 |

移动或重命名 Unity 资源时必须同时移动 `.meta` 文件并保留 GUID，避免场景引用丢失。

## 6. 配置

Go Agent Host：

- `AGENT_HOST_ADDR`
- `AGENT_HOST_BASE_URL`
- `UNITY_JSONRPC_WS_URL`
- `UNITY_TOOL_TIMEOUT_SECONDS`
- `LLM_API_URL`
- `LLM_API_KEY`
- `LLM_MODEL`
- `LLM_REQUEST_TIMEOUT_SECONDS`
- `LLM_MAX_TOOL_ROUNDS`

Unity：

- `UNITY_JSONRPC_WS_URL`
- `UNITY_INSTANCE_ID`
- `PLAYER_ID`

优先级：进程环境变量 > `.env.local` > `.env` > 默认值。禁止提交真实密钥。

## 7. 开发规则

- 网络线程不得调用 Unity API；只向线程安全队列投递命令或 UI 回调。
- 所有 WebSocket 写入必须经过发送锁。
- pending 请求必须支持超时、取消、断线清理和重复响应隔离。
- 新工具必须先在 Unity 注册 Schema 和执行逻辑，再由 Go 动态发现；禁止在 Go 硬编码一份重复 Schema。
- 日志只记录事件、ID、NPC、工具、长度、耗时和结果，不记录玩家正文、模型回复全文或密钥。
- 不为未来功能预建兼容分支。确有版本升级时，先更新 `ARCHITECTURE.md` 并明确迁移和删除日期。

## 8. 验证

Go 修改至少运行：

```text
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

协议修改还要运行：

```text
node GameMCPServer/test_mcp.js --start-server
```

Unity 修改必须完成 C# 编译检查，并在 `SampleScene` 验证：

1. Unity 注册成功。
2. 普通对话能显示最终回复。
3. `game_npc_move` 能到达 `warehouse` 和 `gate`。
4. Go 重启后 Unity 能重连并重新注册。
5. Unity Console 无编译错误、Missing Script 或协议解析异常。
