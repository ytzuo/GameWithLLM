# A2A + MCP 标准化重构架构方案

## 1. 文档状态

- 状态：破坏性重构提案
- 适用范围：专门用于推翻现有内部协议的重构分支
- 当前事实源：重构完成前仍以 `ARCHITECTURE.md` 和现有实现为准
- 目标事实源：方案确认并开始迁移后，应重写 `ARCHITECTURE.md`，最终由新的架构文档替代当前 v2 事实

本文有意放开当前架构中的以下限制：

- 不再要求 `/unity/ws` 永久作为唯一入口；
- 允许删除当前 `protocolVersion: 2` 内部协议；
- 允许使用标准 MCP 的 `tools/list`、`tools/call` 和相关通知；
- 允许引入 A2A 作为玩家与 Agent 之间的消息和任务协议；
- 允许把当前 Go Agent Host 拆成 Agent Service 与 Runtime Gateway；
- 允许重新设计 Unity SDK 的连接模型、会话模型和长时工具模型。

本文仍然保留以下核心边界：

- LLM API Key、模型调用、完整对话历史和 tool loop 只存在于 Agent Service；
- Unity 不直接调用 LLM，不读取 LLM Key；
- Unity 是 GameObject、NavMesh、Inventory、Quest 和真实行为结果的权威来源；
- Agent Service 不直接访问 Unity 对象，也不推断最终世界状态；
- Unity API 只能在 Unity 主线程执行；
- 工具参数必须保持结构化对象语义，不能在网络层二次编码为 JSON 字符串；
- 实时世界状态不能写入静态 NPC Profile；
- 日志不记录玩家正文、模型回复全文、工具完整参数或密钥。

---

## 2. 架构决策摘要

如果从标准化和长期互操作性出发重新设计，本方案不采用“所有消息全部改成 MCP”，而采用分层协议：

```text
玩家 / Unity 与 Agent 的对话：A2A
Agent 对 Unity 世界能力的调用：MCP
远程 Unity 的网络可达性：Runtime Gateway 反向连接
世界存档与对话快照协调：独立 Save Coordination API
```

目标架构：

```text
┌─────────────────────────────────────────────────────────┐
│ Unity Game                                              │
│                                                         │
│ UI / NPC / NavMesh / Inventory / Quest / SaveGame       │
│                         │                               │
│              Unity Agent Runtime SDK                    │
│              ├─ Entity Registry                         │
│              ├─ Tool Registry                           │
│              ├─ Tool Contract                           │
│              ├─ Main Thread Executor                    │
│              ├─ A2A Client                              │
│              ├─ MCP Runtime Server                      │
│              └─ Runtime Transport                       │
└──────────────────────┬──────────────────────────────────┘
                       │
          ┌────────────┴────────────┐
          │                         │
       A2A 对话                  MCP 工具
          │                         │
          ▼                         ▼
┌─────────────────────────────────────────────────────────┐
│ Agent Service                                           │
│                                                         │
│ A2A Server              MCP Client Manager              │
│       │                         │                       │
│       └──── Conversation Engine ┘                       │
│                    │                                    │
│           Profile / Policy / LLM Adapter                │
└─────────────────────────────────────────────────────────┘
```

对于远程部署，增加 Runtime Gateway：

```text
Unity SDK
  │ 出站 WSS 或 gRPC 双向连接
  ▼
Runtime Gateway
  │ 对 Agent Service 暴露逻辑 MCP Endpoint
  ▼
Agent Service MCP Client
```

核心判断：

- A2A 是 Agent 对话和长任务协议；
- MCP 是模型可调用能力协议；
- Runtime Gateway 是网络拓扑适配层，不承担 LLM 决策；
- Save Coordination 是游戏事务，不伪装成模型工具。

---

# 第一部分：现状与重构动机

## 3. 当前两端交互模型

当前系统使用一个项目内部的 JSON-RPC 2.0 WebSocket 协议承载所有语义：

```text
Unity → Go
├── unity.register
├── unity.npc.changed
├── unity.tools.changed
├── conversation.start
├── player.message
├── conversation.end
├── savegame.conversations.save
└── savegame.conversations.load

Go → Unity
├── unity.tool.execute
├── unity.tool.cancel
├── assistant.status
└── assistant.delta
```

一条玩家消息的链路是：

```text
Unity player.message
  ↓
Go ConversationService
  ↓
LLM streaming completion
  ├─ 文本 → assistant.delta
  └─ ToolCall → unity.tool.execute
                    ↓
              Unity 主线程执行
                    ↓
              JSON-RPC ToolResult
                    ↓
              Go 继续 tool loop
  ↓
最终 AssistantReply 作为 player.message response
```

## 4. 当前设计值得保留的部分

本次重构不应推翻以下实现经验：

- Unity 运行时注册是工具能力的事实来源；
- C# 参数类型生成 JSON Schema；
- Go 和 Unity 对工具参数双重校验；
- 网络线程只投递线程安全命令，不直接调用 Unity API；
- 工具执行结果区分业务失败和传输失败；
- pending 请求支持超时、取消、断线清理和重复结果隔离；
- 长时移动只有真实到达后才报告成功；
- Go 管对话，Unity 管世界；
- 工具结果使用结构化数据，不靠自然语言解析状态。

标准化重构的目标是替换协议边界和模块职责，不是重新发明这些正确机制。

## 5. 当前内部协议的局限

### 5.1 对话与工具混在同一协议层

`player.message`、`assistant.delta`、`unity.tool.execute` 和存档事务共享同一个 Gateway Session，导致连接、会话、工具和 UI 生命周期互相影响。

### 5.2 外部互操作性有限

其他 Agent Host 无法直接把 Unity 当成标准工具服务；另一个 Unity 项目也需要复制当前 Gateway DTO 和方法名。

### 5.3 SDK 公共接口容易泄漏内部协议

Unity 业务层当前会看到 `UnityGateway*` DTO、JSON-RPC request ID、协议方法和 pending 语义。

### 5.4 每 NPC 能力与工具目录绑定方式专用

当前协议把 `npcId` 放在 `unity.tool.execute` 的外层，并使用 `npcTools` 映射表达每个 NPC 的能力。这种建模无法直接映射到标准 MCP Tool。

### 5.5 远程可达性没有抽象

当前 Unity 主动建立 WebSocket，适合客户端在 NAT 后运行；标准 MCP Streamable HTTP 通常由 Tool Server 暴露可连接端点。若直接让远程 Agent Service 连接玩家机器，会遇到网络可达性和安全问题。

### 5.6 长时工具模型仍是项目专用

当前 `unity.tool.cancel`、Go pending 和 `ToolExecutionResult.Pending` 可以工作，但无法直接被标准 MCP Client 复用。

---

# 第二部分：目标协议分层

## 6. 为什么不是“全部 MCP”

MCP 适合解决：

- 工具发现；
- JSON Schema 参数；
- 工具调用；
- 工具列表变化；
- 请求进度；
- 请求取消；
- 结构化工具结果。

MCP 不直接定义当前游戏需要的全部应用语义：

- 玩家与某个 NPC 建立对话；
- 多轮 Conversation Context；
- 玩家消息与 Agent 消息；
- 回复流式输出；
- 一轮 Agent 请求的任务状态；
- 游戏实例、玩家和 NPC 的路由；
- 世界存档与对话快照事务。

如果全部塞入 MCP，仍然需要大量自定义 Tool 或 Extension，最终会把应用协议伪装成工具协议。

## 7. A2A 对话平面

A2A 用于 Unity 玩家与 Agent Service 之间的交互。

本方案使用的 A2A 概念：

- Agent Card：发现 Agent Service 能力、端点和认证要求；
- Message：玩家或 Agent 的一轮消息；
- Context：同一玩家与 NPC 的多轮会话上下文；
- Task：一次可能包含工具循环的 Agent 请求；
- Task Status：working、input-required、completed、failed、cancelled；
- Streaming：文本和状态增量；
- Artifact：非普通文本的结构化产物。

当前协议到 A2A 的映射：

| 当前语义 | A2A 目标语义 |
|---|---|
| `conversation.start` | 创建或复用 `contextId` |
| `player.message` | Send Message / Send Streaming Message |
| `assistant.delta` | Streaming Message 或 Artifact chunk |
| `assistant.status` | Task Status Update |
| `conversation.end` | Unity 释放 Context；必要时取消活动 Task |
| 一次玩家请求 | A2A Task |
| 同一 NPC 多轮对话 | A2A Context |
| 最终 AssistantReply | completed Task 中的 Agent Message |

A2A 官方规范将 Message、Task、Context、Artifact 和流式更新作为核心模型，并支持 JSON-RPC、gRPC 和 HTTP+JSON 等协议绑定：

- [A2A 官方规范](https://github.com/a2aproject/A2A/blob/main/docs/specification.md)
- [A2A 官方项目](https://github.com/a2aproject/A2A)

## 8. Game Context A2A Extension

一个 Agent Service 可以服务多个游戏实例、玩家和 NPC。A2A 核心协议不理解这些游戏 ID，因此定义明确的扩展，而不是修改基础字段语义。

扩展 URI：

```text
https://gamewithllm.dev/extensions/game-context/v1
```

建议数据：

```json
{
  "instanceId": "game-instance-123",
  "playerId": "player-1",
  "agentId": "Ryan_001",
  "sceneId": "warehouse-demo"
}
```

约束：

- `instanceId` 标识一次真实 Unity Runtime；
- `playerId` 标识当前玩家；
- `agentId` 标识玩家正在交互的游戏实体；
- `sceneId` 只用于路由和诊断，不作为世界状态事实；
- Agent Service 必须校验这些 ID 是否属于当前认证主体；
- 扩展中不传递玩家消息副本、模型历史或实时世界快照。

不建议为每个 NPC 启动独立 A2A Server。NPC Profile 和 Conversation 仍由同一个 Agent Service 管理，通过 `instanceId + playerId + agentId` 路由。

## 9. MCP 工具平面

Agent Service 作为 MCP Client；Unity Runtime 或 Runtime Gateway 作为 MCP Server。

当前协议到 MCP 的映射：

| 当前语义 | MCP 目标语义 |
|---|---|
| `unity.register` 工具目录 | MCP initialize + `tools/list` |
| `unity.tools.changed` | `notifications/tools/list_changed` |
| `unity.tool.execute` | `tools/call` |
| `unity.tool.cancel` | `notifications/cancelled` |
| 工具执行进度 | `notifications/progress` |
| `ToolResult` | MCP `CallToolResult` |
| 工具业务失败 | `CallToolResult.isError=true` |
| 协议或参数信封错误 | JSON-RPC protocol error |

MCP 官方工具规范定义了 `tools/list`、`tools/call`、JSON Schema 和工具列表变化通知：

- [MCP Tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)
- [MCP Transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
- [MCP Schema](https://modelcontextprotocol.io/specification/2025-11-25/schema)

## 10. MCP Tool 建模

### 10.1 `entityId` 进入工具参数

当前 `npcId` 位于 `unity.tool.execute` 外层。MCP `tools/call` 没有项目专属的 NPC 外层，因此执行实体必须成为工具参数的一部分。

调用示例：

```json
{
  "name": "game_npc_move",
  "arguments": {
    "entityId": "Ryan_001",
    "targetId": "landmark:warehouse",
    "approachDistance": 1.5
  }
}
```

Schema 示例：

```json
{
  "type": "object",
  "properties": {
    "entityId": {
      "type": "string",
      "description": "执行该行为的游戏实体 ID"
    },
    "targetId": {
      "type": "string"
    },
    "approachDistance": {
      "type": "number",
      "minimum": 0
    }
  },
  "required": ["entityId", "targetId"],
  "additionalProperties": false
}
```

SDK 可以把 `entityId` 作为公共前缀参数自动合并到每个实体工具的业务 Schema，避免所有游戏工具手写重复字段。

### 10.2 不为每个 NPC 复制工具

禁止使用：

```text
Ryan_001.game_npc_move
Alice_001.game_npc_move
Bob_001.game_npc_move
```

否则实体数量会直接放大模型工具列表，影响成本、缓存和工具选择质量。

一个工具只注册一次，调用时通过 `entityId` 路由。

### 10.3 动态可用性仍在执行时校验

工具存在不代表所有实体当前都能执行。Unity 在每次调用时重新校验：

```text
Runtime 在线
  + Entity 在线且归属正确
  + Tool 已注册
  + Tool 对 Entity 的 IsAvailable=true
  + 参数满足 Schema
  + 参数满足领域 Validate
  + 当前游戏状态允许执行
```

过期的能力查询不能授予执行权限。

## 11. MCP Resources 与实时世界状态

可以用 MCP Resources 暴露有限的目录型信息：

```text
game://instances/{instanceId}/entities
game://instances/{instanceId}/entities/{entityId}/capabilities
game://instances/{instanceId}/entities/{entityId}/state
game://instances/{instanceId}/item-definitions
```

但应谨慎使用：

- 高频变化的坐标、距离、路径和 Inventory 不应依赖长期缓存；
- 模型准备执行行为前仍应调用实时查询 Tool；
- Resource 只能作为可发现视图，不能取代 Unity 执行时校验；
- 不把整个场景对象树或敏感内部组件暴露为 Resource；
- 资源更新通知要有限流和合并策略。

对于当前项目，第一版可以不实现 Resources，只保留以下查询 Tool：

- `game_entity_get_capabilities`
- `game_npc_get_state`
- `game_scene_get_targets`
- `game_inventory_get_self`

## 12. 长时工具、进度与取消

第一版长时 Tool 使用标准 MCP 在途请求：

```text
tools/call game_npc_move
  ↓
Unity 在主线程开始移动
  ↓
notifications/progress
  ↓
成功、业务失败或取消
  ↓
CallToolResult
```

取消使用 `notifications/cancelled`，关联原 `tools/call` request ID。

Unity SDK 内部统一使用：

```text
MCP request ID
  ↓
ToolInvocation ID
  ↓
CancellationTokenSource
  ↓
领域操作，例如 NavMesh Move Operation
```

约束：

- 收到取消后尽力停止领域行为；
- 已取消请求的迟到结果必须丢弃；
- 每个调用最多回传一次最终结果；
- 进度通知不能包含内部推理或敏感参数；
- 工具自己定义可取消性和建议超时；
- Agent Service 设置上限，Unity 设置领域上限，两者不能使用互相冲突的固定默认值。

MCP Tasks 当前仍处于实验性或扩展演进阶段，第一版不把它作为核心依赖：

- [MCP Tasks Extension](https://modelcontextprotocol.io/extensions/tasks/overview)
- [MCP Request Cancellation and Progress Schema](https://modelcontextprotocol.io/specification/2025-11-25/schema)

当 Tasks Extension 稳定并且目标 Client 明确支持后，可以通过 capability negotiation 增加 task-augmented tool calls，不修改 Unity Tool Core。

---

# 第三部分：部署拓扑

## 13. 本地模式

适用场景：

- PC 单机游戏；
- 开发和测试环境；
- Go Agent Service 作为本地 sidecar；
- Unity 所在平台允许监听 loopback HTTP。

拓扑：

```text
Unity Game
  ├─ A2A Client ───────────────┐
  └─ MCP Streamable HTTP Server│
               ▲               │
               │ localhost     │
               │               ▼
          Local Agent Service / Sidecar
          ├─ A2A Server
          ├─ MCP Client
          └─ LLM Client
```

原则：

- Unity MCP Server 只绑定 `127.0.0.1`；
- 使用随机可用端口或显式配置端口；
- 通过启动握手或本地发现文件传递端点；
- 本地端点仍应使用短期 bearer token；
- 不绑定 `0.0.0.0`；
- Unity 主线程不直接处理 HTTP 回调，只消费调度队列。

优点：

- Agent Service 与 Unity 之间使用标准 MCP；
- 不需要 Runtime Gateway；
- 调试和协议合约测试简单；
- 可以用标准 MCP Client 测试 Unity Tool Runtime。

限制：

- WebGL、移动平台或主机平台可能不允许监听端口；
- 远程 Agent Service 无法直接连接 NAT 后的玩家设备。

## 14. 远程模式

适用场景：

- Agent Service 部署在云端；
- Unity 运行在玩家设备；
- 移动平台、主机平台或 WebGL；
- Unity 不能或不应暴露公网 MCP 端点。

拓扑：

```text
Unity Agent Runtime SDK
  │
  │ 出站双向 WSS/gRPC
  ▼
Runtime Gateway
  ├─ Runtime Connection Registry
  ├─ Instance Authentication
  ├─ Reverse Tool Router
  ├─ MCP Endpoint Virtualization
  └─ Pending / Cancel / Disconnect Isolation
  │
  │ MCP Streamable HTTP
  ▼
Agent Service MCP Client
```

Runtime Gateway 为每个 Unity Runtime 暴露逻辑 MCP Endpoint，例如：

```text
/mcp/runtimes/{instanceId}
```

Agent Service 看到的是 MCP Server；Gateway 把 `tools/list`、`tools/call`、progress 和 cancel 映射到 Unity 的反向连接。

Gateway 不负责：

- 调用 LLM；
- 保存模型对话历史；
- 决定调用什么工具；
- 推断游戏状态；
- 修改 Tool Result。

Gateway 只负责：

- 连接和身份；
- MCP Endpoint 虚拟化；
- 请求关联和路由；
- 取消、断线和超时传播；
- 消息大小、并发和速率限制；
- 结构化可观测性。

## 15. Runtime Bridge 内部协议

远程模式无法完全避免 Unity 到 Gateway 的内部传输协议。该协议的目标不是成为新的公共 Agent 协议，而是承载反向 MCP 执行。

建议特点：

- 使用 WSS 或双向 gRPC；
- Unity 始终主动建立出站连接；
- 消息语义直接对应 MCP initialize、list、call、progress、cancel 和 result；
- 每条消息携带 runtime connection ID 和 invocation ID；
- Tool arguments 保持 JSON object 或等价的结构化字段；
- 支持重新连接后的完整能力重新发布；
- 不在 Bridge 中传输完整对话历史；
- 不在 Bridge 中实现 tool loop。

公共 SDK Tool API 不引用 Bridge DTO。Bridge 只是 `IRuntimeTransport` 的一个实现：

```csharp
public interface IRuntimeTransport
{
    Task StartAsync(RuntimeManifest manifest, CancellationToken cancellationToken);
    IAsyncEnumerable<RuntimeCommand> ReadCommandsAsync(CancellationToken cancellationToken);
    Task SendResultAsync(RuntimeToolResult result, CancellationToken cancellationToken);
    Task SendProgressAsync(RuntimeToolProgress progress, CancellationToken cancellationToken);
}
```

本地 MCP Server 与 Reverse Gateway Transport 共用同一 Tool Runtime。

## 16. 部署模式选择

| 运行环境 | 推荐模式 |
|---|---|
| Unity Editor | 本地 MCP 或 Mock Runtime |
| PC 单机 + 本地模型 Host | 本地 MCP |
| PC 游戏 + 云端 Agent | Runtime Gateway |
| Mobile | Runtime Gateway |
| Console | Runtime Gateway，按平台网络能力适配 |
| WebGL | WebSocket Runtime Gateway |
| CI/协议测试 | Headless Mock Runtime 或本地 MCP |

---

# 第四部分：组件设计

## 17. Unity Agent Runtime SDK

目标包结构：

```text
Packages/com.gamewithllm.agent-runtime/
├── Runtime/
│   ├── Core/
│   ├── Entities/
│   ├── Tools/
│   ├── Threading/
│   ├── Conversations/
│   ├── A2A/
│   ├── MCP/
│   └── Transports/
│       ├── LocalMcp/
│       └── ReverseGateway/
├── Editor/
├── Tests/
│   ├── EditMode/
│   └── PlayMode/
└── Samples~/
    └── WarehouseDemo/
```

### 17.1 Core

- SDK 生命周期；
- Runtime Instance ID；
- 配置和依赖注入；
- 错误类型；
- shutdown 和资源清理。

### 17.2 Entities

```csharp
public interface IAgentEntity
{
    string EntityId { get; }
    bool IsOnline { get; }
}

public interface IGameObjectAgentEntity : IAgentEntity
{
    GameObject GameObject { get; }
}
```

SDK 不引用当前 `NpcEntity`。NPC、宠物、载具、建筑和机关都可以实现 `IAgentEntity`。

### 17.3 Tools

```csharp
public interface IAgentTool
{
    AgentToolDescriptor Descriptor { get; }

    bool IsAvailable(AgentToolContext context);

    ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolContext context,
        AgentJsonObject arguments,
        CancellationToken cancellationToken);
}
```

保留现有优秀能力：

- attribute discovery；
- IL2CPP preserve；
- C# 类型生成 JSON Schema；
- `additionalProperties=false`；
- 结构校验 + 领域校验；
- 结构化业务结果；
- 工具异常隔离。

### 17.4 Threading

```csharp
public interface IAgentMainThreadScheduler
{
    bool IsMainThread { get; }
    ValueTask SwitchToMainThreadAsync(CancellationToken cancellationToken);
}
```

任何 MCP、A2A 或 Gateway 回调都不能直接访问 Unity API。

### 17.5 Conversations

Unity UI 只消费统一事件，不直接消费 A2A DTO：

```csharp
public abstract class AgentResponseEvent { }
public sealed class ResponseStarted : AgentResponseEvent { }
public sealed class TextDelta : AgentResponseEvent { public string Text; }
public sealed class StatusChanged : AgentResponseEvent { public string Status; }
public sealed class ResponseCompleted : AgentResponseEvent { public string FinalText; }
public sealed class ResponseFailed : AgentResponseEvent { public string Code; public string Message; }
```

A2A Adapter 负责把 Task、Message、Artifact 和流式事件转换成这些 SDK 事件。

## 18. Agent Service

Agent Service 由当前 Go Agent Host 演进而来：

```text
Agent Service
├── A2A Server
├── Agent Card Provider
├── Game Context Extension Validator
├── Conversation Engine
├── Session Store
├── Profile Provider
├── Prompt Builder
├── LLM Adapter
├── MCP Client Manager
├── Tool Policy
└── Conversation Snapshot Store
```

职责：

- 接收 A2A Message；
- 按 Context 维护对话；
- 根据 `agentId` 加载 Profile；
- 连接对应 Runtime 的 MCP Endpoint；
- 从 `tools/list` 构建模型工具定义；
- 运行 LLM tool loop；
- 通过 MCP 调用 Unity；
- 通过 A2A 流式输出状态和文本；
- 保存 Agent 自有对话快照。

不再负责：

- 自定义 Unity WebSocket Session；
- `unity.register` 和 `npcTools` 解析；
- `unity.tool.execute` pending；
- Unity 连接本身的反向路由。

## 19. Runtime Gateway

建议作为独立模块实现，初期可以与 Agent Service 同进程部署，但代码边界必须独立。

```text
Runtime Gateway
├── RuntimeTransportServer
├── RuntimeRegistry
├── RuntimeAuthenticator
├── ManifestStore
├── McpEndpointFactory
├── McpToolRouter
├── PendingInvocationRegistry
├── CancellationRouter
└── ConnectionMetrics
```

关键状态：

```text
runtimeConnectionId
instanceId
authenticatedPlayerOrTenant
manifestRevision
connectedAt
lastHeartbeatAt
pendingInvocations
```

断线后：

- 原 connection 的所有 pending invocation 失败；
- MCP Client 获得明确的 runtime unavailable 错误；
- 旧 connection 的迟到结果被隔离；
- 重连产生新 connection generation；
- Unity 完整重新发布 Runtime Manifest；
- Agent Service 不自动假定旧世界状态仍然有效。

## 20. Save Coordination Service

世界存档与 Agent 对话快照是跨权威事务，单独建模：

```text
Unity SaveGame
  ├─ 世界状态
  └─ saveId / operationId

Agent Snapshot Store
  ├─ 对话上下文
  └─ saveId / operationId

Save Coordinator
  └─ prepare / commit / restore 状态
```

建议 API：

```text
POST /game-saves/{saveId}/agent-context:prepare
POST /game-saves/{saveId}/agent-context:commit
POST /game-saves/{saveId}/agent-context:restore
GET  /game-saves/{saveId}/agent-context/status
```

不把 Save 操作注册成 MCP Tool，避免模型自行覆盖或加载存档。

---

# 第五部分：完整交互流程

## 21. 启动流程

### 本地模式

```text
1. Unity 初始化 Tool Registry
2. Unity 注册 Agent Entities
3. Unity 生成 Runtime Manifest
4. Unity 在 loopback 启动 MCP Streamable HTTP Endpoint
5. Unity A2A Client读取 Agent Card
6. Agent Service MCP Client连接 Unity Endpoint
7. MCP initialize 和 capabilities negotiation
8. Agent Service调用 tools/list
9. Unity进入 ready 状态
```

### 远程模式

```text
1. Unity 初始化 Tool Registry 和 Entity Registry
2. Unity 使用短期设备凭证连接 Runtime Gateway
3. Gateway 校验凭证并生成 runtime connection generation
4. Unity 发布 Runtime Manifest
5. Gateway 建立逻辑 MCP Endpoint
6. Unity A2A Client连接 Agent Service
7. Agent Service根据instanceId获取对应 MCP Endpoint
8. MCP initialize 和 tools/list
9. Unity进入 ready 状态
```

## 22. 玩家消息流程

```text
1. 玩家选择 Ryan_001
2. Unity 创建或恢复本地 Conversation Handle
3. Unity 构造 A2A Message
4. Game Context Extension携带instanceId/playerId/agentId
5. Unity发送 streaming message request
6. Agent Service校验身份和 Runtime 所有权
7. Agent Service创建或加载 Context
8. Agent Service加载 Ryan Profile
9. Agent Service开始 A2A Task
10. Unity收到 working 状态
11. Agent Service调用 LLM
```

## 23. 工具调用流程

```text
1. LLM返回结构化 ToolCall
2. Agent Service根据 tools/list 校验工具
3. Agent Service补充或验证 entityId
4. Agent Service通过MCP发送 tools/call
5. MCP Server或Gateway创建 invocation
6. Unity Tool Runtime收到 AgentToolInvocation
7. 调度到Unity主线程
8. 再次检查Entity、Tool、IsAvailable和参数
9. 执行真实游戏行为
10. 可选发送progress
11. 返回CallToolResult
12. Agent Service将结果写回LLM上下文
13. LLM继续调用工具或生成最终回复
```

## 24. 最终回复流程

```text
1. LLM生成最终文本
2. Agent Service生成 A2A streaming update
3. Unity A2A Adapter转换为 TextDelta
4. Chat UI更新当前草稿
5. Agent Service返回最终 Agent Message
6. A2A Task进入completed
7. Unity转换为 ResponseCompleted
8. UI提交最终消息并清理 loading 状态
```

## 25. 取消流程

### 玩家取消 Agent 请求

```text
Unity取消 A2A Task
  ↓
Agent Service取消当前 LLM 请求
  ↓
如果存在 MCP tools/call，则发送 notifications/cancelled
  ↓
Gateway或Unity取消对应 ToolInvocation
  ↓
Unity领域行为停止
  ↓
A2A Task进入cancelled
```

### MCP 请求超时

```text
Agent Service超时
  ↓
发送MCP cancelled
  ↓
Unity取消领域操作
  ↓
Agent Service得到tool execution timeout
  ↓
LLM根据结构化失败决定回复或替代操作
```

## 26. Runtime 断线流程

```text
Unity连接断开
  ↓
Gateway标记runtime offline
  ↓
所有pending MCP calls失败
  ↓
Agent Service停止当前tool loop
  ↓
A2A Task返回明确失败状态
  ↓
Unity重连并完整重新发布Manifest
  ↓
新请求使用新的connection generation
```

---

# 第六部分：安全与可观测性

## 27. 认证和授权

### Unity 到 Runtime Gateway

- 使用短期设备或游戏 Session Token；
- Token 绑定 player/tenant、game build 和允许的 runtime scope；
- 重连时重新校验；
- 不使用提交到仓库的静态共享密钥；
- 生产环境只允许 TLS。

### Unity 到 Agent Service

- A2A Agent Card 声明认证要求；
- Unity 使用当前玩家或游戏 Session 凭证；
- Agent Service 校验 Game Context Extension 的实例、玩家和 Agent 归属。

### Agent Service 到 MCP Endpoint

- 本地模式使用 loopback + 短期随机 token；
- 远程模式使用服务身份和 instance-scoped authorization；
- Agent Service 只能调用其当前 Conversation 所属 Runtime；
- MCP tools/call 中的 `entityId` 必须属于目标 Runtime。

MCP Streamable HTTP 的安全要求包括 Origin 校验、本地仅绑定 loopback，以及为连接实施认证：

- [MCP Transport Security](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)

## 28. 工具授权策略

工具可执行需要同时满足：

```text
MCP Endpoint scope
  + Runtime ownership
  + Entity ownership
  + Tool exists
  + Tool available for Entity
  + Tool argument validation
  + Game semantic validation
  + Optional player confirmation
```

对高影响工具可以声明：

```text
readOnly
destructive
requiresConfirmation
interruptible
longRunning
suggestedTimeout
```

是否要求玩家确认由 Unity UI 和 Agent Service Policy 共同决定，模型不能自行绕过。

## 29. 日志

允许记录：

- trace ID；
- A2A task ID；
- MCP request ID；
- runtime connection generation；
- instance ID、agent/entity ID；
- tool name；
- 参数长度和结果数据长度；
- 耗时；
- outcome 和 error code。

禁止记录：

- LLM API Key 或 token；
- 玩家消息正文；
- 模型回复全文；
- 工具完整 arguments；
- Inventory 或隐私数据完整内容；
- system prompt。

## 30. Trace 传播

建议统一传播：

```text
traceparent
tracestate
baggage（严格限制内容）
```

一次玩家请求应能关联：

```text
A2A Task
  → LLM request
  → MCP tools/call
  → Gateway invocation
  → Unity Tool Operation
```

指标：

- A2A 请求数、首 token 延迟、总耗时；
- 每轮 tool call 数；
- MCP call 成功率和耗时；
- Unity 主线程排队耗时；
- Tool execution 耗时；
- Runtime 在线数量和重连次数；
- cancel、timeout、late-result 数量；
- Schema revision 和 tools/list change 次数。

---

# 第七部分：数据所有权

## 31. 权威数据表

| 数据 | 权威来源 |
|---|---|
| GameObject、Transform、NavMesh | Unity |
| Inventory、Quest、交互状态 | Unity |
| Tool 实际可用性和执行结果 | Unity |
| MCP Tool Schema | Unity Tool Runtime |
| 玩家与 NPC 对话上下文 | Agent Service |
| NPC Profile 和 Prompt | Agent Service |
| LLM 请求和 Tool Loop | Agent Service |
| Runtime 连接和 MCP 路由 | Runtime Gateway |
| Unity 世界存档 | Unity |
| Agent 对话快照 | Agent Service |
| 世界与对话快照提交状态 | Save Coordinator |
| UI 展示缓存 | Unity UI |

## 32. ID 模型

建议稳定区分：

| ID | 生命周期 |
|---|---|
| `installationId` | 一次游戏安装，可选 |
| `playerId` | 玩家账户或本地玩家 |
| `instanceId` | 一次 Unity Runtime 启动 |
| `connectionGeneration` | 一次 Runtime Gateway 连接 |
| `entityId` | 游戏世界中的稳定 Agent Entity |
| `a2aContextId` | 多轮对话上下文 |
| `a2aTaskId` | 一次玩家 Agent 请求 |
| `mcpRequestId` | 一次 MCP 请求 |
| `toolInvocationId` | SDK 内部工具执行 |
| `saveId` | 世界和对话快照关联 ID |
| `operationId` | 一次存档提交操作 |

禁止用一个 ID 同时承担多种生命周期语义。

---

# 第八部分：重构计划

## 33. 阶段 0：冻结现状和建立基线

目标：保证推翻协议时能够判断行为是否回归。

工作项：

- 固定当前 v2 协议合约测试；
- 补 Unity Schema、主线程调度、取消和重连测试；
- 记录当前 SampleScene 玩家流程；
- 修复日志记录完整工具参数的问题；
- 记录当前工具 Schema 快照；
- 建立端到端测试数据和成功标准。

完成标准：

- 当前 Go 测试、vet、race 和协议测试通过；
- Unity EditMode/PlayMode 基线可重复运行；
- 重构前行为有可比较证据。

## 34. 阶段 1：抽取协议无关 Unity Tool Runtime

目标：先解除 Tool Core 对 `NpcEntity` 和 v2 DTO 的依赖。

工作项：

- 建立 UPM 包或独立 asmdef；
- 引入 `IAgentEntity`；
- 引入通用 `AgentToolContext`；
- 把 `ToolsRegistry`、`ToolContract`、Discovery 和 Result 迁入 SDK；
- 引入 `IAgentMainThreadScheduler`；
- 保留旧 v2 Adapter 驱动新 Tool Runtime；
- 当前移动和 Inventory 工具作为 Sample Tool。

完成标准：

- Tool Runtime 不引用 `NpcEntity`、UI、SaveGame 或 v2 DTO；
- 当前 v2 仍能执行所有工具；
- Schema 无非预期变化。

## 35. 阶段 2：实现本地 MCP Server

目标：使用标准 MCP 代替 Go→Unity 工具调用路径。

工作项：

- 实现 MCP initialize；
- 实现 `tools/list`；
- 实现 `tools/call`；
- 实现 `notifications/tools/list_changed`；
- 实现 progress 和 cancellation；
- 自动向业务 Schema 合并 `entityId`；
- Go 新增 MCP Client Manager；
- 将 ToolExecutor 从 v2 pending 改为 MCP call；
- 本地模式先绑定 loopback。

完成标准：

- Go 通过 MCP 调用移动、状态和 Inventory；
- 工具业务失败正确映射 `isError`；
- 取消、断线和迟到结果隔离通过测试；
- Go 不再依赖 `unity.tool.execute` 执行新链路。

## 36. 阶段 3：实现 A2A 对话

目标：使用 A2A 替换自定义 Conversation 协议。

工作项：

- Go 实现 A2A Server 和 Agent Card；
- 定义 Game Context Extension；
- Unity 实现 A2A Client Adapter；
- 映射 Context 和当前 Session；
- 映射 streaming message；
- 映射 Task status、cancel 和 error；
- UI 改为消费统一 `AgentResponseEvent`；
- Conversation Engine 保持原有 tool loop 语义。

完成标准：

- 普通对话通过 A2A 完成；
- Tool loop 通过 A2A + MCP 完成；
- 流式文本不重复、不丢失；
- 玩家可以取消活动 Task；
- Unity 不再依赖 `conversation.start`、`player.message`、`assistant.delta`。

## 37. 阶段 4：实现 Runtime Gateway

目标：支持云端 Agent Service 与 NAT 后 Unity Runtime。

工作项：

- 定义内部 Runtime Bridge 协议；
- Unity 实现 Reverse Gateway Transport；
- Gateway 实现 Runtime Registry；
- Gateway 虚拟化 MCP Endpoint；
- 实现 connection generation；
- 实现身份、授权、限流和消息大小限制；
- 实现 pending、cancel、disconnect 和 reconnect；
- 为 WebGL/Mobile 建立兼容路径。

完成标准：

- 远程 Agent Service 通过 MCP 调用玩家设备中的 Unity Tool；
- Unity 只需出站连接；
- 旧连接迟到结果不能污染重连；
- Runtime 间工具和数据严格隔离。

## 38. 阶段 5：拆分存档协调

目标：从旧 Gateway 协议移除 Save Conversation 方法。

工作项：

- 定义 Save Coordinator API；
- 保留 Unity 世界存档权威；
- Agent Service 提供对话快照 prepare/commit/restore；
- 显式处理部分提交和重试；
- 更新 Save UI；
- 建立故障恢复测试。

完成标准：

- 存档不依赖 v2 WebSocket；
- Unity 和 Agent Snapshot 仍以 saveId 原子关联；
- 部分失败可诊断、可显式重试。

## 39. 阶段 6：删除旧协议

删除内容：

- `/unity/ws` 路由；
- `protocolVersion: 2`；
- `unity.register`；
- `unity.npc.changed`；
- `unity.tools.changed`；
- `unity.tool.execute`；
- `unity.tool.cancel`；
- `conversation.start`；
- `player.message`；
- `conversation.end`；
- `assistant.status`；
- `assistant.delta`；
- `savegame.conversations.save`；
- `savegame.conversations.load`；
- Go `internal/unity` 中只服务旧协议的 DTO 和 Session；
- Unity `UnityGatewayProtocol` 和旧 DTO；
- 临时 dual-stack feature flag。

删除前置条件：

- A2A 对话全链路通过；
- MCP 本地和远程调用通过；
- Save Coordinator 通过；
- SampleScene 所有验收流程通过；
- 新架构文档成为事实源；
- 不再有生产路径依赖旧协议。

本项目不长期维护 v2 与标准化架构双栈。迁移分支允许短期并行验证，完成后删除旧路径。

---

# 第九部分：测试与验收

## 40. Unity SDK 测试

### EditMode

- Tool discovery；
- Schema generation；
- entityId Schema 合并；
- 参数严格校验；
- ToolResult 映射；
- 重复 ID 和重复 Tool；
- MCP Tool descriptor 映射；
- A2A event 映射。

### PlayMode

- 主线程执行；
- Entity 上下线；
- 长时工具；
- progress；
- cancel；
- timeout；
- late result；
- Runtime reconnect；
- 多 Entity 并发隔离。

## 41. Go 测试

- A2A Agent Card；
- Game Context Extension 校验；
- A2A streaming Task；
- A2A cancellation；
- MCP initialize；
- tools/list 缓存和更新；
- tools/call；
- MCP business error 和 protocol error；
- Runtime ownership；
- Conversation tool loop；
- Snapshot coordination；
- race 测试。

## 42. Runtime Gateway 测试

- Runtime authentication；
- connection generation；
- Endpoint virtualization；
- tools/list 路由；
- tools/call 路由；
- progress；
- cancel；
- disconnect；
- reconnect；
- duplicate result；
- cross-runtime isolation；
- rate limit；
- message size limit。

## 43. 端到端验收

至少验证：

1. Unity 能通过 A2A 与 Ryan 普通对话；
2. 流式回复能够正确显示；
3. LLM 能通过 MCP 查询场景目标；
4. LLM 能通过 MCP 移动到 warehouse 和 gate；
5. 移动过程中能报告安全的进度状态；
6. 玩家能取消移动和当前 Agent Task；
7. Inventory 查询和原子转移正确；
8. Unity 重连后 MCP 能力重新可用；
9. 两个 Unity Runtime 之间严格隔离；
10. 世界和对话快照能够保存、失败重试和恢复；
11. Unity Console 无编译错误、Missing Script 和线程异常；
12. 不启动真实 LLM 时可以使用 Mock A2A/MCP 完成 SDK 测试。

---

# 第十部分：风险和待决策项

## 44. 主要风险

### 标准实现复杂度增加

同时引入 A2A、MCP 和 Gateway，比单一内部 WebSocket 更复杂。

控制：先完成本地 A2A + MCP，再实现 Gateway；每个协议只承担适合自己的职责。

### MCP Tool 列表和实体动态能力不完全同构

控制：Tool 全局注册一次，`entityId` 进入参数；执行时重新校验实体能力；需要时通过查询 Tool 或 Resource 提供能力视图。

### A2A 与当前 UI 语义映射差异

控制：Unity UI 只消费 SDK Response Event；所有 A2A Task、Message 和 Artifact 细节限制在 Adapter 内。

### 实验性 MCP Tasks 变化

控制：第一版使用在途 `tools/call + progress + cancelled`，Tasks 仅作为可协商扩展。

### 远程 Gateway 成为关键基础设施

控制：本地模式无需 Gateway；远程模式需要高可用、无状态路由或可恢复连接设计，以及严格指标和容量测试。

### 重构跨度过大

控制：Tool Runtime、MCP、A2A、Gateway、Save 按阶段独立落地；每阶段有明确删除和验收条件。

## 45. 待决策项

进入实现前需要明确：

1. 第一目标平台是 PC 本地还是云端多人；
2. A2A 选择 JSON-RPC、HTTP+JSON 还是 gRPC binding；
3. Unity 本地 MCP Server 使用现成 C# SDK还是自建最小实现；
4. Runtime Gateway 使用 WSS 还是双向 gRPC；
5. `entityId` 是自动注入 Tool Schema 还是由工具参数基类声明；
6. 第一版是否实现 MCP Resources；
7. 玩家取消是取消当前 A2A Task 还是整个 Context；
8. Save Coordinator 与 Agent Service 同进程还是独立服务；
9. Agent Service 与 Runtime Gateway 初期是否同二进制部署；
10. 是否需要支持第三方 MCP Client 直接连接 Unity Runtime；
11. SDK 的最低 Unity 版本和目标平台集合；
12. A2A 和 MCP 的具体协议版本锁定与升级策略。

推荐初始选择：

| 决策 | 推荐值 |
|---|---|
| 第一平台 | Unity Editor + PC 本地模式 |
| A2A binding | HTTP+JSON/SSE 或官方支持最成熟的流式 binding |
| MCP transport | Streamable HTTP，loopback |
| Gateway | 第二阶段之后再实现 |
| Tool entity 参数 | SDK 自动合并 `entityId` |
| MCP Resources | 第一版不实现 |
| 长时工具 | 在途 call + progress + cancelled |
| MCP Tasks | 不作为第一版核心依赖 |
| Save Coordinator | 初期与 Agent Service 同部署、代码独立 |
| 旧协议 | 迁移完成后删除，不长期双栈 |

---

## 46. 最终完成定义

当以下条件全部满足时，可以认为破坏性重构完成：

1. Unity 与 Agent Service 的玩家交互使用 A2A；
2. Agent Service 调用 Unity 世界能力使用 MCP；
3. Unity 游戏工具不引用 A2A、MCP、Gateway 或 Go DTO；
4. 本地模式可以不经过 Runtime Gateway 工作；
5. 远程模式中 Unity 只需建立安全的出站连接；
6. Runtime Gateway 不包含 LLM 和 Conversation Engine；
7. Save Coordination 已从模型工具和旧 WebSocket 协议中分离；
8. LLM Key、历史和 tool loop 仍只在 Agent Service；
9. Unity API 始终只在主线程执行；
10. 当前 v2 协议代码和临时兼容路径已经删除；
11. 新 `ARCHITECTURE.md` 与实现、测试和部署方式一致；
12. Warehouse Sample 和第二个不同玩法 Sample 都能复用同一 SDK。

最终产品形态：

```text
Unity Agent Runtime SDK
  + A2A Conversation Adapter
  + MCP Tool Server
  + Local MCP Transport
  + Reverse Gateway Transport
  + Go Agent Service
  + Runtime Gateway
  + Save Coordination Module
  + Mock A2A/MCP Test Runtime
  + Warehouse Sample
```

该形态的核心价值不是“使用了两个标准”，而是把玩家对话、Agent 决策、游戏工具、网络拓扑和存档事务拆成了可以独立替换和验证的边界。
