# NPC Agent 当前架构与交互流程简要说明

本文用于快速了解当前实现。完整约束、代码地图和验证要求以
[`ARCHITECTURE.md`](./ARCHITECTURE.md) 为准；重构背景见
[`A2A_MCP_REARCHITECTURE.md`](./A2A_MCP_REARCHITECTURE.md)。

## 1. 当前架构

系统由 Unity Runtime、Go Agent Service 和 Runtime Gateway 组成。Gateway
与 Agent Service 当前在同一 Go 进程中装配，但职责保持独立。

```text
Unity Game
├─ UI / NPC / NavMesh / Inventory / SaveGame
├─ Unity Agent Runtime SDK
├─ A2AClientAdapter ───────────────► /a2a
└─ RuntimeGatewayClient ───────────► /runtime/ws
                                          │
Go Agent Service                         │
├─ A2A Server                            │
├─ Conversation Engine / Tool Loop       │
├─ OpenAI-compatible LLM Client          │
├─ MCP Agent Runtime Adapter ◄───────────┤
├─ Runtime Registry / Gateway ───────────┘
├─ Virtual MCP Endpoint
└─ Save Coordinator
```

本地和远程不再是两套实现。Unity 始终主动连接 Runtime Gateway：

- 本地开发：`ws://127.0.0.1:8080/runtime/ws`。
- 远程部署：`wss://agent.example.com/runtime/ws`。

两者使用相同的注册、Manifest、工具调用、进度、取消、重连和代际隔离逻辑，
只改变连接地址和部署级凭据。

关键边界：

- LLM Key、模型请求、完整对话历史和 tool loop 只存在于 Go。
- Unity 不调用 LLM；Go 不直接访问 Unity 对象或推断行为结果。
- Unity API 只在主线程执行，网络回调只向线程安全队列投递命令。
- 工具 Schema 只由 Unity Runtime 生成，Go 不维护第二份目录。
- 工具参数在所有协议边界上都是 JSON 对象。

模块间协议速查：

| 模块连接 | 协议与传输 |
|---|---|
| Unity A2A Client → Go A2A Server | A2A，JSON-RPC 2.0 over HTTP(S)；流式响应为 SSE |
| Unity Runtime → Runtime Gateway | Runtime Bridge，JSON-RPC 2.0 over WebSocket/WSS |
| 外部 MCP Client → Virtual MCP | MCP `2025-11-25`，JSON-RPC 2.0 over HTTP(S) |
| Unity Save Client → Save Coordinator | 项目内 REST/JSON over HTTP(S)，不使用 JSON-RPC |
| Go Agent Service → LLM | OpenAI-compatible Chat Completions over HTTP(S)，流式响应为 SSE |
| Go 内部 Conversation → MCP Adapter → Registry | 同进程 Go 接口，使用 MCP 语义但不发生 HTTP 环回 |

A2A、MCP 和 Runtime Bridge 共享 JSON-RPC 2.0 信封，但方法命名空间和业务
语义相互独立。完整的认证、方向和消息清单见
[`ARCHITECTURE.md 的模块间协议总览`](./ARCHITECTURE.md#2-模块间协议总览)。

## 2. 协议和接口

### 2.1 A2A：玩家对话

| 路由/方法 | 用途 |
|---|---|
| `GET /.well-known/agent-card.json` | 发现 Agent 能力 |
| `POST /a2a — message/send` | 非流式消息 |
| `POST /a2a — message/stream` | SSE 流式 Task、文本和最终状态 |
| `POST /a2a — tasks/cancel` | 取消当前 Task |

玩家消息通过 metadata 携带 `instanceId`、`playerId`、`agentId` 和
`sceneId`。`contextId` 对应 Go 内存中的对话上下文；每次复用都校验
`instanceId + playerId + agentId`，禁止跨 Runtime、玩家或实体复用。

### 2.2 Runtime Bridge：Unity 出站工具通道

Unity 连接 `/runtime/ws` 后：

1. 发送 `runtime.initialize`，携带设备 token 和完整 Runtime Manifest。
2. 工具或实体变化时发送 `runtime.manifest.changed`，完整替换能力快照。
3. Gateway 通过 `runtime.tools.call` 下发工具调用。
4. Runtime 使用 `runtime.progress` 报告安全进度。
5. 取消使用 `runtime.cancelled` 传播。

每次重连都会产生新的 `connectionGeneration`。断线会失败旧连接全部
pending；旧连接迟到的结果不能进入新连接。

### 2.3 MCP：Go 内部工具抽象和可选标准入口

MCP 不再作为 Unity 本地 HTTP Server。Agent Service 通过统一的 MCP
`Client` 抽象访问 Runtime Registry，协议语义包括 `tools/list`、
`tools/call`、取消、进度和 `CallToolResult`。

Gateway 还为需要标准 MCP 接口的服务身份提供：

```text
/mcp/runtimes/{instanceId}
```

该虚拟端点把 MCP 请求路由到已经通过 `/runtime/ws` 注册的目标 Unity。
因此游戏工具仍只定义和注册一次，既不需要本地 MCP Server，也没有本地
fallback。

SDK 会把必填 `entityId` 合并到业务工具 Schema。Agent Service 对模型隐藏
该路由字段，并把当前 A2A `agentId` 安全绑定为 `entityId`。Unity 执行前
再次校验实体、工具、Schema、业务参数和实时游戏状态。

### 2.4 Save Coordination：存档协调

```text
POST /game-saves/{saveId}/agent-context:prepare
POST /game-saves/{saveId}/agent-context:commit
POST /game-saves/{saveId}/agent-context:restore
GET  /game-saves/{saveId}/agent-context:status
```

Unity 保存世界状态；Agent Service 只保存非 system 的对话上下文。两者通过
`saveId + operationId` 关联。存档不是模型工具。

## 3. 主要交互流程

### 3.1 启动与发现

1. Unity 发现 Agent Entity 和工具，生成完整 Runtime Manifest。
2. `RuntimeGatewayClient` 主动连接本地或远程 `/runtime/ws`。
3. Gateway 验证 token，登记 `instanceId`、Manifest 和 connection generation。
4. Agent Service 只有在目标 Runtime 已注册后，才为该实例创建或继续 A2A
   Context。

### 3.2 普通对话

1. Unity 将玩家文本和 Game Context 发送到 `message/stream`。
2. Agent Service 加载 NPC Profile 和 Context，调用 LLM。
3. LLM 不请求工具时，Agent Service 直接通过 SSE 返回文本增量。
4. `A2AClientAdapter` 把协议事件转换为 SDK 响应事件，UI 不依赖 A2A DTO。

### 3.3 带工具的对话

1. Agent Service 从 Runtime Registry 取得工具目录，过滤后暴露给模型。
2. 模型请求工具，例如 `game_npc_move(targetId=landmark:gate)`。
3. Agent Service 校验参数并绑定当前 `entityId`。
4. MCP Runtime Adapter 经 Gateway 发送 `runtime.tools.call`。
5. Unity 网络层把协议无关命令投递到 `CommandDispatcher`。
6. `NpcEntity` 在主线程执行真实 NavMesh 行为并返回结构化结果。
7. Agent Service 将结果写回模型上下文，继续 tool loop。
8. 最终回复通过 A2A SSE 返回 UI。

### 3.4 取消与重连

取消从 A2A Task 传播到 LLM、在途工具调用和 Unity 行为。Unity 会停止对应
NavMesh 行为；一次性 completion 与 connection generation 隔离迟到结果。

Go 或网络恢复后，Unity 重新连接并发布完整 Manifest。Agent Service 不使用
旧连接的 pending 或能力快照。

### 3.5 保存与恢复

保存时 Unity 暂停新对话，等待当前操作结束，捕获世界状态，再依次调用
`prepare` 和 `commit`。恢复时 Unity 先恢复世界和 NPC 集合，Agent Service
再用当前 Profile、Prompt 和 Runtime 能力恢复历史，并返回新的 A2A
`contextId`。

## 4. 配置差异

| 场景 | Runtime 地址 | 传输和代码路径 |
|---|---|---|
| Editor / 本地单机 | `ws://127.0.0.1:8080/runtime/ws` | Runtime Gateway |
| 远程服务 | `wss://agent.example.com/runtime/ws` | Runtime Gateway |

`RUNTIME_GATEWAY_WS_URL` 决定地址，`RUNTIME_GATEWAY_TOKEN` 提供 Runtime
身份。没有 `AGENT_RUNTIME_MODE` 或 Unity 本地 MCP 端口配置。

## 5. 已删除的旧架构

当前实现不再包含：

- `/unity/ws` 和 `protocolVersion: 2`；
- `unity.register`、`unity.tool.execute`、`player.message` 等旧方法；
- Unity `UnityGatewayProtocol` / `UnityGatewayClient`；
- Unity 本地 `McpRuntimeServer`；
- 本地/远程模式分支以及本地 MCP fallback；
- Go `internal/unity`。

## 6. 相关文档

- [`ARCHITECTURE.md`](./ARCHITECTURE.md)：当前架构事实源。
- [`A2A_MCP_REARCHITECTURE.md`](./A2A_MCP_REARCHITECTURE.md)：重构决策和历史阶段记录。
- [`.env.example`](./.env.example)：统一 Runtime、A2A、LLM 和存档配置示例。
