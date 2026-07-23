# NPC Agent 系统

基于 Unity + Go 的双进程 NPC Agent 系统：玩家在 Unity 中发起对话，Go Agent Host 调用大模型并决定是否执行工具，Unity 在主线程执行真实游戏行为并返回结果。

## 架构概览

```
┌─────────────────────────┐     WebSocket JSON-RPC      ┌──────────────────────┐
│      Unity (执行端)      │ ◄─────────────────────────► │   Go Agent Host     │
│                         │     /unity/ws                │                      │
│  • UI / 玩家交互         │                              │  • LLM 对话编排      │
│  • NPC 生命周期          │                              │  • 工具决策 & 校验    │
│  • 游戏行为执行           │                              │  • Session 管理      │
│  • 能力声明 & 注册        │                              │  • Unity 实例注册    │
└─────────────────────────┘                              └──────────────────────┘
```

**核心原则：Go 决策，Unity 执行。**

- **Go** 是智能决策和会话状态的权威来源：维护对话 Session、调用 LLM、运行 tool-call 循环
- **Unity** 是游戏世界和行为结果的权威来源：管理 NPC、GameObject、NavMesh，在主线程执行行为

详细架构说明见 [ARCHITECTURE.md](./ARCHITECTURE.md)。

## 项目结构

| 目录 | 职责 |
|---|---|
| `GameMCPServer/` | Go Agent Host：LLM、Session、工具策略、参数校验、Unity 注册与命令调度 |
| `unity-NPC-agent-client/` | Unity 执行端：UI、NPC 生命周期、运行时能力声明、主线程游戏行为 |

## 环境要求

- **Go** >= 1.26
- **Unity** 6000.3.x（编辑器版本 6000.3.19f1）
- **Node.js**（用于协议测试脚本）

## 快速开始

### 1. 克隆并配置环境变量

```bash
git clone <repo-url> GameWithLLM
cd GameWithLLM
```

从模板创建 `.env.local` 并填入配置：

```bash
cp .env.example .env.local
```

编辑 `.env.local`，必须配置的项：

```env
# LLM 配置（必填）
LLM_API_URL=https://api.openai.com/v1/chat/completions
LLM_API_KEY=sk-your-key-here
LLM_MODEL=gpt-4o-mini
```

> `.env.example` 中列出了所有可用配置项及其默认值。`.env.local` 不会被提交到 Git。

### 2. 启动 Go Agent Host

```bash
# 方式一：使用 Makefile
make server

# 方式二：直接运行
cd GameMCPServer
go run ./cmd/server
```

服务默认监听 `:8080`，提供：
- `/unity/ws` — Unity WebSocket 入口
- `/health` — 健康检查

### 3. 打开 Unity 项目

1. 使用 Unity Hub 打开 `unity-NPC-agent-client` 目录
2. 打开 `SampleScene` 场景
3. 点击 Play 运行

Unity 会自动连接 Go Agent Host 并完成注册。之后在场景中与 NPC 对话即可体验。

### 4. 验证系统正常

对话中可尝试：

- 普通闲聊 — 模型直接回复文本
- `让 NPC 移动到 warehouse` — 触发 `game_npc_move` 工具调用，NPC 会沿 NavMesh 移动到仓库

## 配置参考

### Go Agent Host

| 变量 | 说明 | 默认值 |
|---|---|---|
| `AGENT_HOST_ADDR` | HTTP 监听地址 | `:8080` |
| `AGENT_HOST_BASE_URL` | 对外访问 URL | `http://127.0.0.1:8080` |
| `UNITY_JSONRPC_WS_URL` | Unity WS 连接地址 | `ws://127.0.0.1:8080/unity/ws` |
| `UNITY_TOOL_TIMEOUT_SECONDS` | 工具执行超时（秒） | `10` |
| `LLM_API_URL` | LLM API 端点 | `https://api.openai.com/v1/chat/completions` |
| `LLM_API_KEY` | LLM API 密钥 | — |
| `LLM_MODEL` | 模型名称 | `gpt-4o-mini` |
| `LLM_REQUEST_TIMEOUT_SECONDS` | LLM 请求超时（秒） | `60` |
| `LLM_MAX_TOOL_ROUNDS` | 单轮对话最大工具调用次数 | `4` |

### Unity

| 变量 | 说明 | 默认值 |
|---|---|---|
| `UNITY_JSONRPC_WS_URL` | Go Agent Host WebSocket 地址 | `ws://127.0.0.1:8080/unity/ws` |
| `UNITY_INSTANCE_ID` | Unity 实例标识 | `local-game-1` |
| `PLAYER_ID` | 玩家标识 | `local-player-1` |

配置优先级：**进程环境变量 > `.env.local` > `.env` > 默认值**。

## 开发

### Go 测试与检查

```bash
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

### 协议测试

```bash
node GameMCPServer/test_mcp.js --start-server
```

### 给项目添加新工具

1. 创建继承 `ToolArgsBase` 的参数类型并实现 `Validate`
2. 创建带 `[NpcTool]` 和 `[Preserve]` 的 `NpcTool<TArgs>` 工具类，在类中声明名称、描述、Schema 和执行适配
3. 工具会在 Unity 启动时通过反射自动注册；连接建立后，Go 通过 `unity.register` 动态发现工具

> **禁止**在 Go 侧硬编码重复的 Schema。工具能力的唯一来源是 Unity 运行时注册。

## 关键约束

- LLM API Key、对话历史、tool loop 仅存在于 Go 进程
- Unity 不直接请求 LLM，不持有 API Key
- Go 不直接操作 Unity GameObject
- Unity API 调用必须在主线程执行
- 工具参数在网络协议中必须是 JSON 对象，不得二次编码为 JSON 字符串
- 当前会话存储为内存实现，Go 重启后对话不恢复

更多约束与规则见 [agents.md](./agents.md)。

## 协议

两端通过 WebSocket JSON-RPC 2.0 协议通信，唯一入口为 `/unity/ws`。当前协议版本为 `protocolVersion: 1`。

9 个协议方法：

| 方法 | 方向 | 说明 |
|---|---|---|
| `unity.register` | Unity→Go | 注册实例、工具和能力 |
| `unity.npc.changed` | Unity→Go | NPC 上下线变更 |
| `unity.tools.changed` | Unity→Go | 工具能力动态变更 |
| `conversation.start` | Unity→Go | 发起新对话 |
| `player.message` | Unity→Go | 玩家消息 |
| `conversation.end` | Unity→Go | 结束对话 |
| `unity.tool.execute` | Go→Unity | 要求执行工具 |
| `unity.tool.cancel` | Go→Unity | 取消工具执行 |
| `assistant.status` | Go→Unity | 推送助手状态 |

## License

待定
