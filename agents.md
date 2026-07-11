# agents.md

## 项目总览

这个仓库是一个 Unity + Go 的轻量 monorepo，目标是把“LLM 对话 / tool_calls”安全地映射为 Unity 里 NPC 的实际行为。

整体分成两部分：

| 部分 | 职责 |
|---|---|
| `GameMCPServer` | Go 侧宿主服务，负责协议入口、工具声明、请求校验、WebSocket 转发、结果回传 |
| `unity-NPC-agent-client` | Unity 侧客户端，负责连接宿主、调用 LLM、接收工具命令、在主线程执行 NPC 行为 |

---

## 1. GameMCPServer

### 1.1 角色定位

`GameMCPServer` 是独立运行的 Go 服务，定位为：

- MCP 风格的工具宿主
- Unity 命令转发网关
- 请求-响应追踪节点

它不直接操作 Unity 对象，只通过 JSON-RPC + WebSocket 把工具调用发给 Unity。

### 1.2 启动与配置

入口在 `GameMCPServer/cmd/server/main.go`：

- 读取配置：`internal/config/config.go`
- 注册路由：`internal/handler/router.go`
- 启动 HTTP 服务：`http.ListenAndServe`

配置来源优先级：

1. 进程环境变量
2. 根目录 `.env.local`
3. 根目录 `.env`
4. 默认值

常用变量：

- `MCP_SERVER_ADDR`
- `MCP_BASE_URL`
- `UNITY_JSONRPC_WS_URL`
- `UNITY_TOOL_TIMEOUT_SECONDS`

### 1.3 对外接口

当前代码实现了三个入口：

- `/ws`：显式 WebSocket 入口
- `/`：根路径，普通请求返回运行提示；WebSocket 升级时也进入协议处理
- `/health`：健康检查

### 1.4 协议实现

服务端使用的是轻量 JSON-RPC WebSocket 实现，核心文件在 `internal/unity/`：

- `protocol.go`：JSON-RPC 消息结构
- `websocket.go`：手写 WebSocket 握手和帧读写
- `session.go`：会话循环、pending 请求匹配、超时控制
- `tools.go`：工具声明
- `server.go`：WebSocket / 根路径入口处理

会话模型：

- 每个连接维护一个 `jsonRPCSession`
- `tools/list` 直接返回工具表
- `tools/call` 校验 `npcId`、工具名和参数后转发
- 结果通过同一个 `id` 对应回传

### 1.5 当前工具

当前暴露的工具只有一个：

- `game_npc_move`

参数 schema：

- `targetLandmark`
- 枚举值：`warehouse`、`gate`

### 1.6 设计特点

- 只负责协议与转发，不负责游戏逻辑
- 用 `id` 追踪请求和响应
- 有超时保护
- 工具 schema 与 Unity 侧保持一致

---

## 2. unity-NPC-agent-client

### 2.1 角色定位

`unity-NPC-agent-client` 是 Unity 侧执行端，负责：

- 读取根目录配置
- 连接宿主 WebSocket
- 向 LLM 发起聊天请求
- 接收 `tools/list` / `tools/call`
- 把命令投递到主线程 NPC
- 把执行结果回传给宿主

### 2.2 核心运行入口

核心脚本是 `Assets/Scripts/McpClient/McpAsyncClient.cs`：

- `Start()` 时加载 `.env` / `.env.local`
- 建立 `ClientWebSocket`
- 初始化 `HttpClient`
- 持续接收宿主消息

它同时承担：

- LLM 对话循环
- MCP 工具调用链路
- WebSocket 请求/响应匹配

### 2.3 主要模块

| 模块 | 文件 | 职责 |
|---|---|---|
| 网络与会话 | `McpAsyncClient.cs` | 连接宿主、收发 JSON、驱动 LLM 会话 |
| 工具注册 | `CommandDispatcher/ToolsRegistry.cs` | 注册工具、输出给宿主/LLM 的 schema |
| 命令路由 | `CommandDispatcher/CommandDispatcher.cs` | 把网络消息按 `npcId` 投递到对应 NPC |
| NPC 实体 | `GameLogic/NpcEntity.cs` | 主线程消费命令并执行行为 |
| 参数校验 | `CommandDispatcher/McpToolWrapper.cs` | JSON 反序列化、Validate、异常隔离 |
| 工具参数 | `GameLogic/McpToolArgs/MoveArgs.cs` | `game_npc_move` 的强类型参数 |
| 数据契约 | `Models/Models.cs` | LLM 消息、tool call、response DTO |
| 历史管理 | `HistoryManager/*` | NPC 对话历史的读写接口与文件实现 |
| 单例基类 | `Tools/Singleton.cs` | 统一单例模式 |
| 共享状态 | `SharedDataInstance.cs` | 持久化全局数据容器 |

### 2.4 工具注册与发现

`ToolsRegistry` 是 Unity 侧的工具中心，负责：

- 注册工具名
- 保存 description 和 JSON Schema
- 输出给宿主的 `tools/list`
- 输出给 LLM 的 `tools`

当前注册的工具是：

- `game_npc_move`

### 2.5 NPC 执行流程

`NpcEntity` 是实际执行者：

1. `Start()` 时向 `CommandDispatcher` 注册自己
2. 收到命令后进入私有队列
3. `Update()` 中消费队列
4. 使用 `McpToolWrapper<MoveArgs>` 做参数校验和异常隔离
5. 调用 `NavMeshAgent.SetDestination`
6. 通过 `McpAsyncClient.SendMcpResponseAsync()` 回传结果

### 2.6 对话与 LLM 循环

`McpAsyncClient` 内部的典型流程是：

1. 玩家对某个 NPC 发起交互
2. 组装 `LlmMessage` 历史
3. 调用 OpenAI Chat Completions
4. 若模型返回 `tool_calls`，就发给宿主
5. 等待宿主返回工具结果
6. 把结果塞回对话上下文
7. 继续请求 LLM，直到生成最终回复

### 2.7 历史记录

历史系统是可替换的：

- `HistoryManager` 负责统一入口
- `FileHistoryProvider` 默认把每个 NPC 的对话保存在 `Application.persistentDataPath/npc_history/<npcId>.json`

### 2.8 关键约束

- 网络线程不直接操作 Unity API
- 所有游戏对象操作留在主线程
- 命令与结果通过 `transactionId` / `callId` 对应
- 参数必须先过 `Validate()`

---

## 3. 两部分如何协作

完整链路是：

1. 玩家在 Unity 里和某个 NPC 交互
2. Unity 侧把对话发给 LLM
3. LLM 返回 `tool_calls`
4. Unity 侧把工具调用发给 `GameMCPServer`
5. `GameMCPServer` 校验后转发回 Unity
6. Unity 主线程里的 NPC 执行行为
7. Unity 把执行结果返回给 `GameMCPServer`
8. 结果再回到 LLM，生成最终回复

---

## 4. 文件级关注点

### `GameMCPServer`

- `cmd/server/main.go`
- `internal/config/config.go`
- `internal/handler/router.go`
- `internal/handler/health.go`
- `internal/unity/server.go`
- `internal/unity/session.go`
- `internal/unity/websocket.go`
- `internal/unity/protocol.go`
- `internal/unity/tools.go`

### `unity-NPC-agent-client`

- `Assets/Scripts/McpClient/McpAsyncClient.cs`
- `Assets/Scripts/CommandDispatcher/CommandDispatcher.cs`
- `Assets/Scripts/CommandDispatcher/ToolsRegistry.cs`
- `Assets/Scripts/CommandDispatcher/McpToolWrapper.cs`
- `Assets/Scripts/GameLogic/NpcEntity.cs`
- `Assets/Scripts/GameLogic/McpToolArgs/MoveArgs.cs`
- `Assets/Scripts/HistoryManager/HistoryManager.cs`
- `Assets/Scripts/HistoryManager/FileHistoryProvider.cs`
- `Assets/Scripts/Models/Models.cs`
- `Assets/Scripts/SharedDataInstance.cs`
- `Assets/Scripts/Tools/Singleton.cs`

---

## 5. 现状备注

仓库里的设计文档描述了更完整的 MCP / Unity 交互蓝图；当前代码已经跑通的是一个更轻量的版本，重点是：

- WebSocket JSON-RPC 通道
- `tools/list` / `tools/call`
- 单个 `game_npc_move` 工具
- Unity 主线程安全执行

