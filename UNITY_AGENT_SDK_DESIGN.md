# Unity Agent Runtime SDK 架构与改造方案

## 1. 文档目的

本文基于当前 `GameWithLLM` 实现，说明：

1. Unity 与 Go Agent Host 当前如何协作；
2. 如果把 Unity 端的大模型交互能力封装为可复用 SDK，目标架构应该是什么；
3. 如何在不破坏现有功能和协议边界的前提下分阶段完成改造。

本文中的“Unity Agent SDK”不是把大模型推理迁入 Unity。LLM API Key、模型请求、对话历史和 tool loop 仍然只存在于后端。SDK 的职责是让 Unity 游戏以统一方式连接 Agent 后端、声明游戏能力、在主线程执行工具并消费回复事件。

`ARCHITECTURE.md` 仍是当前实现的架构事实源。本文描述的是目标设计和迁移计划；在改造真正改变协议或数据所有权之前，必须先同步更新 `ARCHITECTURE.md`。

---

## 2. 结论摘要

当前项目已经具备抽成 Unity SDK 的主要基础：

- 工具以独立类声明；
- 通过反射自动发现工具；
- 从 C# 参数类型生成 JSON Schema；
- 按 NPC 运行时状态动态声明能力；
- WebSocket 网络线程与 Unity 主线程明确隔离；
- 支持结构化工具结果、长时执行、超时和取消；
- Go 与 Unity 的数据所有权边界清晰。

当前不适合直接发布为通用 SDK，主要因为：

- 工具上下文和路由直接依赖 `NpcEntity`；
- `AgentHostClient` 混合了后端连接、会话、UI、存档和能力同步职责；
- JSON-RPC v2 方法和 DTO 会泄漏到上层业务代码；
- 长时工具的生命周期由 `NpcEntity` 特殊管理；
- SDK 缺少独立程序集、Mock Backend、编辑器诊断和 Unity 自动化测试。

推荐目标形态：

```text
Unity Agent Runtime SDK
  + JsonRpcV2 Backend Adapter
  + 默认 Go Agent Host
  + Mock Backend
  + Warehouse Sample
```

第一阶段不修改当前线上协议。现有 `/unity/ws` 和 `protocolVersion: 2` 被封装到默认 Backend Adapter 内部，游戏业务只依赖通用的 Agent、Conversation、Tool 和 Event 接口。

---

## 3. 不可破坏的设计边界

SDK 化过程中继续遵守以下边界：

- LLM API Key、模型调用、对话历史和 tool loop 只存在于后端；
- Unity 不直接请求 LLM，也不保存任何 LLM Key；
- Go 不直接访问 Unity 对象，不推断 GameObject 最终状态；
- Unity API 只能在 Unity 主线程执行；
- 工具参数在网络协议中必须是 JSON 对象，不能二次编码为 JSON 字符串；
- 工具 Schema 的事实来源仍然是 Unity 运行时注册；
- 游戏行为失败继续使用结构化业务结果，传输协议错误使用传输层错误；
- 当前会话存储仍是内存实现，不因 SDK 化增加数据库、TTL 或自动恢复；
- 当前唯一 WebSocket 入口仍为 `/unity/ws`；
- 不恢复 `/ws`、`tools/list`、`tools/call` 或 legacy 协议；
- 不为了 SDK 化强行改造成标准 MCP。JSON-RPC 2.0 可以继续作为默认适配器的传输协议。

---

# 第一部分：现有架构

## 4. 当前系统总体结构

当前系统采用 Unity + Go 双进程架构，核心原则是：

> Go 决策，Unity 执行。

```text
玩家
  │
  ▼
Unity UI
  │ conversation.start / player.message
  ▼
Go Agent Host
  │
  ├─ Session 与对话上下文
  ├─ NPC Profile 与 system prompt
  ├─ LLM 流式调用
  ├─ Tool loop
  └─ 工具策略与参数校验
          │
          │ unity.tool.execute
          ▼
Unity Runtime
  ├─ NPC 和世界状态
  ├─ 主线程工具执行
  ├─ NavMesh
  ├─ Inventory
  └─ 结构化执行结果
```

### 4.1 Go Agent Host 职责

Go 当前负责：

- 加载并校验 NPC Profile；
- 创建和管理内存 Session；
- 生成 NPC system prompt；
- 调用 OpenAI-compatible Chat Completions；
- 聚合流式文本和结构化 tool calls；
- 根据 Unity 注册的工具定义向模型暴露能力；
- 校验工具名、权限和 JSON Schema 参数；
- 将工具调用路由到正确 Unity 实例和 NPC；
- 等待工具完成、处理超时和取消；
- 将工具结果写回模型上下文并继续 tool loop；
- 显式保存和加载 Go 自有对话快照。

主要实现位于：

- `GameMCPServer/internal/agent/conversation.go`
- `GameMCPServer/internal/agent/llm_client.go`
- `GameMCPServer/internal/tools/`
- `GameMCPServer/internal/unity/`

### 4.2 Unity 执行端职责

Unity 当前负责：

- 玩家与 NPC 的交互入口和聊天 UI；
- WebSocket 连接、注册、断线重连和 pending 请求；
- 反射发现工具并生成工具 Schema；
- 维护在线 NPC 和每 NPC 工具能力快照；
- 将网络命令投递到 Unity 主线程；
- 按 NPC 路由工具调用；
- 执行 NavMesh、Inventory 等真实游戏行为；
- 返回稳定的结构化结果；
- 保存和加载 Unity 世界状态。

主要实现位于：

- `Assets/Scripts/Networking/AgentHostClient.cs`
- `Assets/Scripts/Networking/UnityGatewayClient.cs`
- `Assets/Scripts/CommandDispatcher/`
- `Assets/Scripts/GameLogic/NpcEntity.cs`
- `Assets/Scripts/Tools/`

## 5. 当前工具识别与注册流程

### 5.1 工具声明

一个工具通常由参数类型和工具类型组成：

```csharp
public sealed class MoveArgs : ToolArgsBase
{
    [ToolParameter(Required = true)]
    public string targetId;

    public override bool Validate(out string errorMessage)
    {
        // 补充结构 Schema 无法表达的业务校验。
    }
}

[NpcTool]
[Preserve]
public sealed class MoveNpcTool : NpcTool<MoveArgs>
{
    public override string Name => "game_npc_move";
    public override string Description => "使 NPC 前往指定目标附近。";

    public override bool IsAvailable(NpcToolContext context)
    {
        return context.Npc.GetComponent<NavMeshAgent>() != null;
    }

    protected override ToolExecutionResult ExecuteCore(
        NpcToolContext context,
        MoveArgs args)
    {
        context.Npc.MoveToTarget(args);
        return ToolExecutionResult.Pending();
    }
}
```

### 5.2 反射发现

`NpcToolDiscovery` 扫描当前 AppDomain 中的程序集，选择：

- 非抽象类；
- 实现 `INpcTool`；
- 带 `[NpcTool]`；
- 能通过公共无参构造函数实例化。

发现后由 `ToolsRegistry` 统一注册。`[Preserve]` 防止 IL2CPP 裁剪只通过反射引用的工具类型。

### 5.3 Schema 生成

`ToolContract<TArgs>` 从参数类型的公共字段和属性生成 JSON Schema，支持：

- string、bool、integer、number、enum；
- 数组和嵌套对象；
- required；
- minimum、maximum；
- 字符串长度、正则和允许值；
- 数组长度、唯一元素和元素约束；
- `additionalProperties: false`。

同一份契约同时用于：

- 注册给 Go 和模型的工具 Schema；
- Unity 执行前的严格参数校验和反序列化。

### 5.4 每 NPC 动态能力

`ToolsRegistry.GetToolNamesForNpc` 对每个 NPC 调用工具的 `IsAvailable`。

例如：

- 移动工具要求 NPC 拥有可用的 `NavMeshAgent`；
- Inventory 工具要求 NPC 拥有 `InventoryComponent`；
- 组件启用状态改变后，NPC 会重新计算能力并通知 Gateway。

最终 Unity 注册给 Go 的能力快照包括：

```text
全局工具目录：tool name + description + inputSchema
在线实体列表：npcs
实体能力映射：npcId -> tool names
```

## 6. 当前两端完整交互流程

### 6.1 启动和注册

```text
Unity 启动
  ↓
ToolsRegistry 反射发现工具
  ↓
CommandDispatcher 收集在线 NPC
  ↓
为每个 NPC 计算可用工具
  ↓
UnityGatewayClient 连接 /unity/ws
  ↓
unity.register
  ↓
Go 校验 protocolVersion、实例、NPC、工具和 npcTools 一致性
  ↓
注册成功
```

断线重连后，Unity 使用新的连接重新注册完整快照。能力变化通过 `unity.npc.changed` 和 `unity.tools.changed` 同步。

### 6.2 普通对话

```text
玩家选择 NPC
  ↓
conversation.start
  ↓
Go 创建 Session 并绑定 playerId、npcId、instanceId
  ↓
玩家输入
  ↓
player.message
  ↓
Go 调用 LLM
  ↓
assistant.delta 流式通知
  ↓
Unity 更新聊天 UI
  ↓
Go 以原 player.message ID 返回最终 AssistantReply
```

普通消息与工具调用不是通过文本内容区分，而是通过 JSON-RPC 信封区分：

| 类型 | JSON-RPC 形态 | Unity 处理方式 |
|---|---|---|
| 文本增量 | `method=assistant.delta`，无 `id` | 更新流式文本 |
| 最终普通回复 | 无 `method`，`id` 匹配原 `player.message` | 完成会话请求 |
| 工具调用 | `method=unity.tool.execute`，有新的 `id` | 路由到工具执行链路 |
| 工具取消 | `method=unity.tool.cancel`，无 `id` | 取消对应执行请求 |

### 6.3 工具调用

```text
LLM 返回结构化 ToolCalls
  ↓
Go Policy 校验工具是否属于当前 NPC 能力
  ↓
Go 按 Unity 注册的 Schema 校验 arguments
  ↓
Go 发送 unity.tool.execute
  ↓
UnityGatewayClient 在网络线程解析协议消息
  ↓
AgentHostClient 转交 CommandDispatcher
  ↓
CommandDispatcher 写入线程安全队列
  ↓
Unity Update 在主线程按 npcId 找到 NpcEntity
  ↓
NpcEntity 私有队列串行取出命令
  ↓
ToolsRegistry 按名称查找工具并再次检查 IsAvailable
  ↓
GameToolWrapper 按同一 Schema 校验并反序列化
  ↓
ToolArgsBase.Validate 执行业务语义校验
  ↓
ExecuteCore 操作真实 Unity 世界
  ↓
Unity 返回 ToolResult
  ↓
Go 将结果作为 role=tool 写入模型上下文
  ↓
LLM 继续下一轮或生成最终回复
```

### 6.4 长时工具与取消

立即查询工具直接返回 `ToolExecutionResult.Success` 或 `Failure`。

移动工具返回 `ToolExecutionResult.Pending`，由 `NpcEntity` 保存当前命令，并在每帧检查：

- 是否到达目标；
- 路径是否失败；
- 是否超时；
- 目标是否销毁；
- 是否收到 `unity.tool.cancel`。

Go 在工具 Context 超时或取消后发送 `unity.tool.cancel`。Unity 清理对应请求并避免回传迟到结果。

### 6.5 存档

当前存档分属两个权威来源：

- Unity 保存玩家/NPC Transform、Inventory 等世界状态；
- Go 保存对话快照；
- 两者仅通过 Unity 生成的 canonical UUID `saveId` 关联。

保存和恢复协议当前也是 `UnityGatewayClient` 的一部分：

- `savegame.conversations.save`
- `savegame.conversations.load`

## 7. 当前架构的优点

- Go 和 Unity 的职责边界明确；
- Unity 运行时能力是工具事实来源；
- 新工具无需在 Go 硬编码重复 Schema；
- 网络线程不会直接访问 Unity API；
- 所有 WebSocket 写入均串行化；
- pending 支持超时、取消、断线清理和重复响应隔离；
- 工具参数在 Go 和 Unity 双重校验；
- 业务失败与协议失败区分明确；
- 长时移动等待真实完成后才向模型报告结果；
- 对话历史与游戏世界状态分别由正确的一端负责。

## 8. 当前阻碍 SDK 化的耦合点

### 8.1 实体类型耦合

`NpcToolContext` 直接保存 `NpcEntity`，`CommandDispatcher` 也直接维护 `npcId -> NpcEntity`。

影响：

- 工具只能面向当前 NPC 类；
- 难以支持宠物、载具、机关、建筑或 ECS Entity；
- SDK 必须引用示例项目的业务组件。

### 8.2 后端协议泄漏

Unity 上层代码直接认识：

- JSON-RPC 方法名；
- `protocolVersion: 2`；
- `UnityGateway*` DTO；
- request ID 和 pending 语义；
- `/unity/ws` 连接地址。

影响：替换后端或传输方式时，游戏业务、会话和工具路由都可能被迫修改。

### 8.3 `AgentHostClient` 职责过多

当前 `AgentHostClient` 同时负责：

- Gateway 生命周期；
- Session 映射；
- 玩家消息提交；
- 流式 UI 回调；
- 工具命令转发；
- NPC 与工具能力快照；
- 存档期间的会话同步；
- 重连后的 Session 清理。

这使 SDK 核心依赖当前 UI 和存档工作流。

### 8.4 长时工具耦合

`ToolExecutionResult.Pending` 没有通用完成句柄。移动的 pending 状态由 `NpcEntity` 特殊保存和驱动。

影响：制作、开门、攻击、动画、采集等长时行为需要复制类似逻辑。

### 8.5 会话事件未统一

文本输出同时由：

- `assistant.delta` 通知；
- `player.message` 最终响应；
- `reset` 草稿撤回；
- 保留但尚未完整接入的 `assistant.status`。

上层 UI 需要了解当前协议的组合规则。

### 8.6 并发范围过大

当前 Unity 会话发送使用全局 `_conversationSendLock`。一个 NPC 的长时间请求可能阻塞其他 NPC 的消息提交。

Go 已经按 Session 串行处理，因此 Unity 更适合使用每 Conversation 或每 Agent 的发送锁，而不是整个客户端共享一把锁。

### 8.7 超时模型不统一

Go 当前使用全局工具超时；Unity 移动工具拥有独立的最大移动时间。默认情况下 Go 工具超时可能早于 Unity 行为超时。

SDK 应允许工具声明通用执行元数据，例如：

- 是否只读；
- 是否长时运行；
- 是否可取消；
- 建议超时时间。

### 8.8 测试与诊断不足

Go 已有较完整的单元和协议测试，但 Unity 工具契约、主线程调度、取消、库存原子操作和重连缺少独立的 EditMode/PlayMode 测试层。当前也没有可以独立于真实 Go 和 LLM 调试工具的 Mock Backend。

---

# 第二部分：目标架构

## 9. SDK 产品定位

Unity Agent Runtime SDK 的职责定义为：

> 让 Unity 游戏对象以安全、动态、主线程可控的方式向外部 Agent Backend 暴露游戏能力，并以统一事件模型消费后端回复。

SDK 核心不关心：

- 后端使用哪种语言；
- 后端使用哪个模型供应商；
- 后端如何构造 prompt；
- 对话历史如何存储；
- tool loop 如何实现；
- 游戏使用 NavMesh、行为树还是其他玩法系统。

SDK 只关心：

- 当前有哪些 Agent Entity；
- 每个 Entity 当前有哪些 Tool；
- 如何安全地执行 Tool；
- 如何与一个 Agent Backend 建立 Conversation；
- 如何接收文本、状态、完成和失败事件；
- 如何取消正在执行的后端请求和 Unity 操作。

## 10. 目标分层

```text
┌───────────────────────────────────────────────┐
│ Game Layer                                    │
│ NPC / NavMesh / Inventory / Quest / UI / Save │
└───────────────────────┬───────────────────────┘
                        │ 通用 SDK API
┌───────────────────────▼───────────────────────┐
│ Unity Agent Runtime SDK                       │
│                                               │
│ Agent Entities      Conversation API          │
│ Tool Contracts      Response Events           │
│ Tool Runtime        Main Thread Scheduler      │
│ Capability Runtime  Backend Abstractions       │
└───────────────────────┬───────────────────────┘
                        │ IAgentBackend
┌───────────────────────▼───────────────────────┐
│ Backend Adapters                              │
│ JsonRpcV2Backend / MockBackend / FutureBackend│
└───────────────────────┬───────────────────────┘
                        │ 具体传输协议
┌───────────────────────▼───────────────────────┐
│ Go Agent Host 或其他 Agent Backend            │
└───────────────────────────────────────────────┘
```

核心原则：

- 游戏层不引用 JSON-RPC DTO；
- SDK Core 不引用 `NpcEntity`、Inventory、NavMesh、UI 或 SaveGame；
- Backend Adapter 负责通用 SDK 语义与具体协议之间的转换；
- 当前 JSON-RPC v2 作为默认 Adapter 保留；
- Mock Backend 与真实 Backend 使用相同公共接口。

## 11. 推荐模块

### 11.1 Core

负责：

- SDK 生命周期；
- Agent Entity 注册；
- Agent ID 和 Instance ID；
- 组合 Tool Runtime、Conversation 和 Backend；
- 统一错误模型。

### 11.2 Tools

负责：

- 工具声明与反射发现；
- 参数契约和 Schema 生成；
- 工具可用性；
- 严格参数校验；
- 主线程执行；
- 取消、超时和结构化结果；
- 工具执行观测事件。

### 11.3 Conversations

负责：

- 创建和关闭 Conversation；
- 发送玩家输入；
- 统一流式事件；
- 当前响应取消；
- 每 Conversation 的串行化。

### 11.4 Backends

负责：

- 定义 `IAgentBackend`；
- 当前 JSON-RPC v2 实现；
- Mock Backend；
- 后端连接状态和错误归一化；
- 可选能力检测。

### 11.5 Threading

负责：

- 网络线程向主线程切换；
- 主线程命令队列；
- CancellationToken 传播；
- Unity 生命周期退出时的清理。

### 11.6 Persistence（可选模块）

负责：

- 对话快照保存和恢复的通用接口；
- 当前 `savegame.conversations.*` 的 Adapter 实现。

该模块不进入 SDK Core，后端不支持持久化时可以不安装或不启用。

### 11.7 Editor（可选模块）

负责：

- 显示 Backend 连接状态；
- 显示已注册实体和工具；
- 预览 JSON Schema；
- 检测重复 ID、重复工具名、缺少 `[Preserve]`；
- 使用 Mock Backend 或本地调用器测试工具；
- 显示请求 ID、耗时、结果和错误码，但不记录玩家正文或完整参数。

## 12. 目标公共接口

以下接口用于说明职责边界，具体命名可在实现阶段调整。

### 12.1 Agent Entity

```csharp
public interface IAgentEntity
{
    string AgentId { get; }
    bool IsOnline { get; }
}

public interface IGameObjectAgentEntity : IAgentEntity
{
    GameObject GameObject { get; }
}

public interface IAgentCommandTarget : IAgentEntity
{
    void Enqueue(AgentToolInvocation invocation);
}
```

SDK 以 `IAgentEntity` 为核心，不要求所有实体必须是当前 `NpcEntity`。当前 NPC 可以通过实现接口或 Adapter 接入。

### 12.2 Tool

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

```csharp
public sealed class AgentToolDescriptor
{
    public string Name { get; init; }
    public string Description { get; init; }
    public AgentJsonObject InputSchema { get; init; }
    public AgentToolExecutionHints ExecutionHints { get; init; }
}

public sealed class AgentToolExecutionHints
{
    public bool ReadOnly { get; init; }
    public bool LongRunning { get; init; }
    public bool Interruptible { get; init; }
    public TimeSpan? SuggestedTimeout { get; init; }
}
```

`AgentJsonObject` 表示 SDK 内部的“根节点必须为 JSON object”的通用值类型。其内部第一阶段可以继续使用 Newtonsoft JSON，但公共语义不包含 JSON-RPC，也不允许网络层二次编码。

### 12.3 Backend

```csharp
public interface IAgentBackend : IAsyncDisposable
{
    AgentBackendState State { get; }

    event Action<AgentBackendState> StateChanged;

    Task ConnectAsync(
        AgentRuntimeManifest manifest,
        CancellationToken cancellationToken);

    Task<IAgentConversation> StartConversationAsync(
        AgentConversationOptions options,
        CancellationToken cancellationToken);
}
```

### 12.4 Conversation

```csharp
public interface IAgentConversation : IAsyncDisposable
{
    string ConversationId { get; }
    string AgentId { get; }

    IAsyncEnumerable<AgentResponseEvent> SendAsync(
        AgentUserMessage message,
        CancellationToken cancellationToken);

    Task CancelCurrentResponseAsync(
        CancellationToken cancellationToken);
}
```

如果目标 Unity API/运行时对 `IAsyncEnumerable` 支持不理想，可以在实现阶段提供等价的事件订阅接口，但上层语义保持一致。

### 12.5 Response Event

```csharp
public abstract class AgentResponseEvent { }

public sealed class ResponseStarted : AgentResponseEvent { }
public sealed class TextDelta : AgentResponseEvent
{
    public string Text { get; init; }
}
public sealed class TextReset : AgentResponseEvent { }
public sealed class StatusChanged : AgentResponseEvent
{
    public string Status { get; init; }
}
public sealed class ResponseCompleted : AgentResponseEvent
{
    public string FinalText { get; init; }
}
public sealed class ResponseFailed : AgentResponseEvent
{
    public string Code { get; init; }
    public string Message { get; init; }
}
```

聊天 UI 不再区分 `assistant.delta`、最终 JSON-RPC response 或后端错误信封，只处理统一的 `AgentResponseEvent`。

### 12.6 Main Thread Scheduler

```csharp
public interface IAgentMainThreadScheduler
{
    bool IsMainThread { get; }

    ValueTask SwitchToMainThreadAsync(
        CancellationToken cancellationToken);
}
```

工具执行链必须在调用 Unity API 前显式进入主线程。`async` 只用于统一长时操作和取消模型，不意味着允许在线程池操作 Unity 对象。

## 13. 当前协议适配器

第一阶段继续使用当前协议，仅将其封装为：

```text
GameAgent.Backends.JsonRpcV2
├── JsonRpcV2Backend
├── JsonRpcV2Transport
├── JsonRpcV2Protocol
├── JsonRpcV2Dtos
└── JsonRpcV2Persistence
```

映射关系：

| SDK 语义 | 当前协议实现 |
|---|---|
| Connect | WebSocket `/unity/ws` |
| PublishManifest | `unity.register` |
| EntityChanged | `unity.npc.changed` |
| CapabilitiesChanged | `unity.tools.changed` |
| StartConversation | `conversation.start` |
| Send user message | `player.message` |
| TextDelta | `assistant.delta` |
| StatusChanged | `assistant.status` |
| Execute tool | `unity.tool.execute` |
| Cancel tool | `unity.tool.cancel` |
| Complete response | `player.message` 的 JSON-RPC response |
| Save conversation | `savegame.conversations.save` |
| Restore conversation | `savegame.conversations.load` |

对当前 Go Host 来说，第一阶段没有协议变化。变化只发生在 Unity 内部：JSON-RPC DTO 不再越过 Adapter 边界。

## 14. 目标工具调用流程

```text
Backend Adapter 收到后端命令
  ↓
转换为 AgentToolInvocation
  ↓
AgentToolRuntime 校验 invocation 和 Agent 所有权
  ↓
EntityRegistry 定位 IAgentCommandTarget
  ↓
ToolRegistry 查找工具并重新检查 IsAvailable
  ↓
按 ToolContract 校验 JSON object 参数
  ↓
MainThreadScheduler 切换 Unity 主线程
  ↓
ExecuteAsync(context, arguments, cancellationToken)
  ↓
得到 AgentToolResult
  ↓
Backend Adapter 转换为后端需要的响应格式
```

SDK Tool Runtime 不应知道：

- JSON-RPC 方法名；
- Go pending map；
- Go Session 类型；
- LLM ToolCall ID 的具体格式；
- UI 如何显示工具状态。

## 15. 长时工具目标模型

目标是由 SDK 统一管理长时操作，而不是由 `NpcEntity` 管理协议 request。

推荐语义：

- 工具返回 `ValueTask<AgentToolResult>`；
- 每次调用都携带 `CancellationToken`；
- SDK 维护 invocation ID 与执行任务；
- 后端取消、超时、实体下线和应用退出都取消同一个 token；
- 工具自己负责停止 NavMesh、动画、制作等领域行为；
- SDK 负责只回传一次最终结果并隔离迟到完成。

移动工具可以逐步改造成：

```csharp
protected override async ValueTask<AgentToolResult> ExecuteCoreAsync(
    AgentToolContext context,
    MoveArgs args,
    CancellationToken cancellationToken)
{
    IMovementCapability movement = context.Require<IMovementCapability>();
    MoveResult result = await movement.MoveToAsync(
        args.targetId,
        cancellationToken);
    return AgentToolResult.FromMoveResult(result);
}
```

第一阶段可以通过 Adapter 包装当前 `Pending` 实现，避免一次性重写移动逻辑。

## 16. 能力发布目标模型

SDK 内部使用通用 Manifest：

```text
AgentRuntimeManifest
├── sdkVersion
├── instanceId
├── tools[]
└── entities[]
    ├── agentId
    └── toolNames[]
```

原则：

- Tool Descriptor 仍由 Unity 运行时生成；
- Entity 的实际工具集合仍由 `IsAvailable` 决定；
- Backend Adapter 决定如何把 Manifest 映射到具体后端协议；
- 能力变化按完整快照还是增量发送，由 Adapter 决定；
- SDK Core 不硬编码 `unity.register` 或 `npcTools` 字段名。

## 17. 对话持久化目标模型

对话存档不是所有 Agent Backend 的必备能力，因此设计为可选接口：

```csharp
public interface IAgentConversationPersistence
{
    Task<AgentSnapshotSaveResult> SaveAsync(
        AgentSnapshotKey key,
        CancellationToken cancellationToken);

    Task<AgentSnapshotRestoreResult> RestoreAsync(
        AgentSnapshotKey key,
        IReadOnlyCollection<string> agentIds,
        CancellationToken cancellationToken);
}
```

当前 JsonRpcV2 Backend 实现该接口。Mock Backend 可以提供内存实现，其他后端可以声明不支持。游戏 Save UI 通过能力检测决定是否启用对话同步。

Unity 世界存档本身仍属于游戏业务，不进入 SDK。

## 18. Go 端在目标架构中的位置

Go 端继续作为默认 Agent Backend：

```text
Unity Agent Runtime SDK
          │
JsonRpcV2 Backend Adapter
          │
       /unity/ws
          │
Go Agent Host
```

第一阶段 Go 无需修改。

后续如果需要让 Go Host 也成为独立可复用组件，可以在 Go 内部继续抽象：

- Conversation Engine；
- Backend Protocol Session；
- Runtime Capability Registry；
- Tool Executor；
- Profile/Prompt Provider。

但这不是 Unity SDK 第一阶段的前置条件，也不应阻塞 Unity 端解耦。

## 19. 推荐包结构

最终建议形成 UPM Package：

```text
Packages/com.gamewithllm.agent-runtime/
├── package.json
├── Runtime/
│   ├── Core/
│   ├── Entities/
│   ├── Tools/
│   ├── Conversations/
│   ├── Threading/
│   ├── Backends/
│   │   └── JsonRpcV2/
│   └── Persistence/
├── Editor/
├── Tests/
│   ├── EditMode/
│   └── PlayMode/
└── Samples~/
    └── WarehouseDemo/
```

在真正移动文件前，先通过 Assembly Definition 验证依赖方向：

```text
AgentRuntime.Core
AgentRuntime.Tools          -> Core
AgentRuntime.Conversations  -> Core
AgentRuntime.JsonRpcV2      -> Core + Tools + Conversations
Game.Runtime                -> AgentRuntime.*
Game.UI                     -> Game.Runtime + AgentRuntime.Conversations
```

SDK 程序集不得反向引用 `Game.Runtime`、UI、Inventory、NavMesh 或 SaveGame。

---

# 第三部分：改造计划

## 20. 改造原则

- 先抽象内部边界，再移动目录和发布 Package；
- 第一阶段保持网络协议和外部行为不变；
- 每个阶段都保持 SampleScene 可运行；
- 先用 Adapter 包装旧实现，再逐步替换内部实现；
- 不同时重写协议、工具系统和 UI；
- 协议或数据所有权真正发生变化前，先更新 `ARCHITECTURE.md`；
- 新旧实现不长期并存，不预建 legacy 兼容分支；
- 使用第二个独立示例验证抽象，而不是只靠接口设计判断通用性。

## 21. 阶段 0：建立安全基线

### 目标

在重构前固定现有行为和验证手段。

### 工作项

- 记录当前完整注册和对话流程；
- 为当前工具 Schema 生成增加快照测试；
- 为未知字段、错误类型、缺失必填项增加 Unity EditMode 测试；
- 为主线程队列、取消和重复结果隔离增加测试；
- 为 Inventory 原子转移增加测试；
- 为重连后重新注册增加 PlayMode 或可控集成测试；
- 保留现有 Go 测试、race 测试和协议测试；
- 清理聊天 UI 中直接展示内部工具名的行为；
- 日志不再记录完整工具参数，只保留 ID、NPC、工具、长度、耗时和结果。

### 验收标准

- `go test ./...`、`go vet ./...`、`go test -race ./...` 通过；
- 当前协议测试通过；
- Unity 核心工具契约拥有 EditMode 测试；
- SampleScene 的现有对话、移动、库存、重连和存档流程不变。

## 22. 阶段 1：拆分程序集和职责，不改协议

### 目标

在当前目录内形成 SDK Core 与 Game Layer 的编译边界。

### 工作项

- 新建 `AgentRuntime.Core.asmdef`；
- 新建 `AgentRuntime.JsonRpcV2.asmdef`；
- 新建游戏业务程序集；
- 把 `AgentHostClient` 拆分为：
  - `AgentRuntime`：SDK 生命周期；
  - `AgentCapabilityPublisher`：能力快照；
  - `ConversationCoordinator`：会话管理；
  - `ToolCommandRouter`：工具命令路由；
  - `SaveGameConversationCoordinator`：游戏存档协调；
  - `ChatPresenter`：UI 适配；
- 保留当前 `UnityGatewayClient` 和协议 DTO，但移入 JsonRpcV2 程序集；
- 使用 Facade 暂时维持当前场景序列化引用，避免场景大规模丢失引用。

### 验收标准

- SDK 程序集不引用 UI、Inventory、NavMesh 或 SaveGame；
- 游戏层可以继续使用原有 Go Host；
- 网络报文与当前 v2 协议一致；
- 场景和 Prefab 不出现 Missing Script；
- 重连和能力重新注册行为不变。

## 23. 阶段 2：通用化 Entity 和 Tool Runtime

### 目标

解除 SDK 对 `NpcEntity` 的直接依赖。

### 工作项

- 引入 `IAgentEntity` 和 `IAgentCommandTarget`；
- 将 `NpcToolContext` 重构为 `AgentToolContext`；
- 将 `INpcTool`/`NpcTool<T>` 逐步重命名为通用 Agent Tool 类型；
- 提供旧命名的短期内部迁移适配，但不形成长期兼容层；
- 将 `CommandDispatcher` 重构为 `AgentEntityRegistry + ToolCommandRouter`；
- 当前 `NpcEntity` 实现通用接口；
- Tool 的 `IsAvailable` 改为依赖通用 Context 或能力接口；
- 保持工具名称、Schema 和当前 Go 注册内容不变。

### 验收标准

- SDK Core 不引用 `NpcEntity`；
- 创建一个非 NPC 测试实体也能注册工具并执行；
- 现有移动和 Inventory 工具行为不变；
- 工具能力变化仍能同步到 Go；
- 工具 Schema 不发生非预期变化。

## 24. 阶段 3：抽象 Backend 与 Conversation

### 目标

让游戏业务不再直接认识 JSON-RPC v2。

### 工作项

- 定义 `IAgentBackend`；
- 定义 `IAgentConversation`；
- 定义统一 `AgentResponseEvent`；
- 将 `UnityGatewayClient` 包装为 `JsonRpcV2Backend`；
- 将 `assistant.delta`、`reset` 和最终 response 归一为通用事件；
- 将 JSON-RPC 错误映射为 SDK 错误类型；
- 将 ToolCall 协议 DTO 转换为内部 `AgentToolInvocation`；
- UI 只订阅 Conversation 事件；
- 增加 `MockAgentBackend`，支持：
  - 固定普通回复；
  - 流式文本；
  - 工具调用；
  - 工具失败；
  - 取消；
  - 模拟断线。

### 验收标准

- Game UI 不引用 `UnityGateway*` DTO；
- 不启动 Go 也能通过 Mock Backend 演示聊天和工具；
- 切换 Mock/JsonRpcV2 Backend 不修改游戏工具；
- 当前 Go Host 不需要协议修改；
- `assistant.delta` 和最终 response 不会造成重复 UI 消息。

## 25. 阶段 4：统一长时工具与取消

### 目标

让移动、制作、动画等长时行为使用同一执行模型。

### 工作项

- 引入 SDK 级 Tool Operation Manager；
- 工具调用统一携带 `CancellationToken`；
- 工具接口支持异步最终结果；
- 将当前 `Pending` 先包装，再逐步迁移；
- 把协议 request 状态从 `NpcEntity` 移到 Operation Manager；
- 处理后端取消、工具超时、实体下线、断线和应用退出；
- 引入 `AgentToolExecutionHints`；
- 根据工具建议超时配置后端执行等待，避免全局超时与领域超时冲突；
- 将 Unity 全局 Conversation 锁调整为每 Conversation 串行。

### 验收标准

- 任意长时工具都能取消；
- 每个 invocation 只返回一次最终结果；
- 取消后迟到结果不会污染新请求；
- 一个 NPC 的长时行为不会阻塞其他 NPC 的普通消息；
- 移动到远目标时不会因不合理的全局默认值提前取消。

## 26. 阶段 5：可选能力、编辑器工具和打包

### 目标

形成可被另一个 Unity 项目消费的 SDK。

### 工作项

- 将对话持久化移入可选接口；
- 当前 JsonRpcV2 Adapter 实现 Persistence 扩展；
- 增加 Agent Runtime Editor Window；
- 增加工具 Schema 预览和本地调用器；
- 增加注册状态、实体能力和执行耗时诊断；
- 建立正式 UPM Package；
- 提供 `package.json`、README、安装说明和最小示例；
- 将 Warehouse 场景整理为 Sample；
- 新增第二个与 Inventory/NavMesh 不同的示例，例如“守卫验证条件后开门”；
- 在第二个项目或隔离样例中验证 Package 安装。

### 验收标准

- SDK 可通过 UPM 安装到一个空 Unity 项目；
- 空项目不需要复制当前 SampleScene 业务代码；
- 新项目只实现 Entity、Tool 和 UI 适配即可连接后端；
- 第二个示例不修改 SDK Core；
- Package 中不存在真实密钥和项目专属配置；
- 文档明确线程、取消、Schema 和数据所有权约束。

## 27. 当前类型到目标类型的迁移映射

| 当前类型 | 目标位置或类型 | 处理方式 |
|---|---|---|
| `AgentHostClient` | 多个 Runtime/Coordinator/Presenter | 拆分职责 |
| `UnityGatewayClient` | `JsonRpcV2Backend` | 包装并内聚协议 |
| `UnityGatewayProtocol` | `JsonRpcV2Protocol` | 仅 Adapter 内可见 |
| `UnityGateway*DTO` | JsonRpcV2 内部 DTO | 不进入公共 API |
| `NpcToolContext` | `AgentToolContext` | 解除 `NpcEntity` 依赖 |
| `INpcTool` | `IAgentTool` | 通用化命名和上下文 |
| `NpcTool<TArgs>` | `AgentTool<TArgs>` | 保留 Schema 自动生成能力 |
| `NpcToolDiscovery` | `AgentToolDiscovery` | 通用化命名 |
| `ToolsRegistry` | `AgentToolRegistry` | 保留动态能力逻辑 |
| `CommandDispatcher` | `AgentEntityRegistry + ToolCommandRouter` | 拆分注册与执行路由 |
| `NpcEntity` 私有命令队列 | `IAgentCommandTarget` 实现 | 游戏层保留 |
| `ToolExecutionResult.Pending` | SDK Tool Operation | 逐步迁移 |
| `ChatViewModel` | Game UI | 不进入 SDK |
| `SaveGameService` | Game Save | 不进入 SDK |
| Inventory/NavMesh 工具 | Sample/Game Tools | 不进入 SDK Core |

## 28. 测试策略

### 28.1 SDK EditMode 测试

- 工具反射发现；
- 重复工具名；
- Schema 生成；
- required、enum、范围和数组约束；
- `additionalProperties=false`；
- 严格反序列化；
- 工具异常映射；
- Entity 和 Tool 动态能力变化；
- Backend DTO 不泄漏到公共程序集。

### 28.2 SDK PlayMode 测试

- 网络线程投递到主线程；
- Entity 上下线；
- 工具调用、取消和超时；
- 断线清理；
- 重连后重新注册；
- 长时工具只完成一次；
- 多 Conversation 不互相阻塞。

### 28.3 Adapter 合约测试

对 `JsonRpcV2Backend` 和 `MockAgentBackend` 使用同一组行为测试：

- Connect；
- StartConversation；
- 流式回复；
- TextReset；
- ToolCall；
- ToolResult；
- Cancel；
- Error；
- Disconnect。

### 28.4 Go 和端到端测试

继续运行：

```text
cd GameMCPServer
go test ./...
go vet ./...
go test -race ./...
```

协议相关修改继续运行：

```text
node GameMCPServer/test_mcp.js --start-server
```

## 29. 主要风险与控制措施

### 风险一：过度抽象

表现：为了支持尚不存在的后端和实体类型，设计大量未被验证的接口。

控制：

- 当前 Go Host 作为第一个真实 Backend；
- Mock Backend 作为第二个接口实现；
- 第二个游戏示例验证 Entity 和 Tool 抽象；
- 未被两个消费者验证的能力不承诺稳定公共 API。

### 风险二：重构期间协议和业务同时变化

表现：难以定位回归来自 SDK 重构还是协议升级。

控制：第一至第三阶段保持当前 v2 报文不变，先在 Unity 内部引入 Adapter。

### 风险三：异步工具误用线程池

表现：工具改为 `Task` 后在非主线程访问 Unity API。

控制：SDK Tool Runtime 在进入工具逻辑前统一切换主线程，并通过 PlayMode 测试验证。

### 风险四：场景序列化引用丢失

表现：移动 MonoBehaviour 或改类名后场景出现 Missing Script。

控制：

- 移动 Unity 文件时同步移动 `.meta`；
- 保留 GUID；
- 第一阶段优先使用 Facade；
- 每阶段在 SampleScene 检查引用和 Console。

### 风险五：兼容层长期残留

表现：同时维护 `NpcTool` 和 `AgentTool`、旧 Client 和新 Backend 两套实现。

控制：每个迁移阶段明确删除旧入口的完成条件，不把临时 Adapter 设计成永久 legacy 模式。

## 30. 最小可行 SDK 范围

第一个可发布版本只需要包含：

- `IAgentEntity`；
- 通用 Tool 声明、发现、Schema 和执行；
- 主线程调度；
- `IAgentBackend`；
- `IAgentConversation`；
- 统一 Response Event；
- JsonRpcV2 Backend；
- Mock Backend；
- EditMode/PlayMode 测试；
- 一个 Warehouse Sample。

以下内容可以后置：

- 对话持久化扩展；
- Editor Runtime Window；
- 第二种真实网络 Backend；
- Go Host 的进一步库化；
- NPC 自主日程、任务或关系系统。

## 31. 最终验收标准

当以下条件全部满足时，可以认为 Unity 端已经形成独立 SDK：

1. SDK Core 不引用 `NpcEntity`、NavMesh、Inventory、UI 或 SaveGame；
2. 游戏业务不引用 JSON-RPC 方法名和 `UnityGateway*` DTO；
3. 当前 Go Host 通过 JsonRpcV2 Backend 正常工作；
4. Mock Backend 无需 Go 和真实 LLM 即可驱动聊天与工具；
5. 新增 Tool 不需要修改 Backend Adapter 或 Go Schema；
6. 新增一种 Agent Entity 不需要修改 SDK Core；
7. 工具执行始终发生在 Unity 主线程；
8. 长时工具支持超时、取消、断线和迟到结果隔离；
9. 两个不同游戏示例可以复用同一个 SDK；
10. SDK 可以通过 UPM 安装到一个独立 Unity 项目；
11. 当前架构文档、SDK 文档、实现和测试保持一致。

---

## 32. 推荐的第一批实施任务

建议先完成一个不修改协议的短周期迭代：

1. 新建 SDK 和 Game Layer 的 Assembly Definition；
2. 从 `AgentHostClient` 拆出 Chat UI 和 SaveGame 协调；
3. 引入 `IAgentEntity`，让 `NpcEntity` 实现；
4. 引入 `AgentToolContext`，但保持现有工具名和 Schema；
5. 把 `UnityGatewayClient` 包装到 `JsonRpcV2Backend` 后面；
6. 建立 `AgentResponseEvent`，统一 delta、reset 和 final reply；
7. 实现最小 `MockAgentBackend`；
8. 为 Schema、主线程调度、工具取消和重连补 Unity 测试；
9. 完成 SampleScene 回归验证；
10. 根据实际改动同步更新 `ARCHITECTURE.md`。

完成这批任务后，项目仍使用同一个 Go Host 和 v2 协议，但 Unity 游戏代码已经基本不感知具体后端协议，后续才能安全推进长时工具统一和正式 UPM 打包。
