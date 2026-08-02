# NPC Agent 系统

基于 Unity 与 Go 的双进程 NPC Agent 系统。玩家在 Unity 中发起对话，Go
Agent Service 调用大模型并运行工具循环，Unity 在主线程执行真实游戏行为并
返回结构化结果。

架构原则是：**Go 决策，Unity 执行**。

```text
Unity Game
├─ A2AClientAdapter ── HTTP/SSE ─────────► Go Agent Service ──► LLM
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
| `POST /a2a` | A2A JSON-RPC 2.0；流式响应为 SSE | 玩家消息、Task 和取消 |
| `GET /runtime/ws`（WebSocket Upgrade） | Runtime Bridge JSON-RPC 2.0 | Unity 注册、工具调用、结果和取消 |
| `POST /mcp/runtimes/{instanceId}` | MCP `2025-11-25` JSON-RPC 2.0 | 可选的服务端 MCP 入口 |
| `/game-saves/{saveId}/agent-context:*` | REST/JSON | 对话快照协调 |
| `GET /health` | HTTP | 健康检查 |

旧 `/unity/ws` 和 `protocolVersion: 2` 协议已经删除。

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
`/runtime/ws`。玩家消息通过 `/a2a` 发送。

## 示例能力

- 普通 NPC 对话与流式回复
- 查询 NPC 状态和可移动目标
- 使用 NavMesh 移动到 `warehouse` 或 `gate`
- 查询、放入和取出 Inventory 物品
- 协调保存和恢复 Unity 世界与 Agent 对话快照

工具 Schema 只由 Unity 运行时生成。新增工具时：

1. 定义继承 `ToolArgsBase` 的参数类型和约束。
2. 实现 `IAgentTool`，或继承游戏适配基类 `NpcTool<TArgs>`。
3. 使用 SDK 的 `[AgentTool]` 标记可发现工具。
4. 由 `ToolsRegistry` 反射发现、生成 Schema，并合并路由字段 `entityId`。
5. Go 从 Runtime Manifest 动态取得工具，禁止再硬编码一份 Schema。

SDK 的范围和接入方式见
[Agent Runtime README](./unity-NPC-agent-client/Packages/com.gamewithllm.agent-runtime/README.md)。

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
