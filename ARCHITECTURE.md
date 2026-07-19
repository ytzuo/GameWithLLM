# NPC Agent 系统架构说明

## 总体架构

系统采用 Unity + Go 的双进程架构，核心原则是“Go 决策，Unity 执行”。

Go Agent Host 负责大模型调用、对话 Session、工具决策、参数校验和请求追踪；Unity 负责玩家交互、NPC 实体状态以及实际游戏行为。两端通过 `/unity/ws` 上的 WebSocket JSON-RPC 2.0 协议通信。

## Go Agent Host

Go 是智能决策和会话状态的权威来源，主要包含：

- `agent`：维护对话 Session，调用 LLM，并运行 tool-call 循环。
- `tools`：根据 Unity 注册的能力生成模型可见工具，完成参数和策略校验。
- `unity`：维护 Unity 实例、NPC 和工具能力快照，并将工具命令路由到正确连接。
- `handler`：提供 `/unity/ws`、`/health` 和根路径 HTTP 入口。

Go 不直接操作 Unity 对象，也不保存游戏世界的实时权威状态。当前 Session 使用内存存储，Go 重启后不会恢复旧对话。

## Unity 执行端

Unity 是游戏世界和行为结果的权威来源，主要包含：

- `AgentHostClient`：连接对话 UI 与 Go Agent Host。
- `UnityGatewayClient`：负责 WebSocket 连接、注册、重连和协议收发。
- `ToolsRegistry`：声明当前实际可执行的工具及其 Schema。
- `CommandDispatcher`：把网络命令投递到目标 NPC 的主线程队列。
- `NpcEntity`：调用 NavMesh 等 Unity API 执行行为，并返回稳定的工具结果。

Unity 不直接调用 LLM，不保存 LLM API Key，也不维护模型对话历史。

## 核心交互链路

1. Unity 连接 Go，并注册实例、NPC 和工具能力。
2. 玩家在 Unity 中与 NPC 交互，Unity 向 Go 创建对话并提交消息。
3. Go 调用 LLM；如果模型直接回复，结果返回 Unity 展示。
4. 如果模型请求工具，Go 校验参数和权限后向 Unity 下发 `unity.tool.execute`。
5. Unity 在主线程执行 NPC 行为并返回结果。
6. Go 将工具结果写回模型上下文，再生成最终回复返回 Unity。

## 数据所有权

- 对话、模型请求和工具决策：Go 权威。
- NPC、GameObject、NavMesh 和行为结果：Unity 权威。
- 实际可执行工具 Schema：Unity 提供，Go 按会话和 NPC 筛选。
- UI 消息列表：Unity 只保存展示缓存，不作为模型历史。

系统只使用当前内部协议版本 `protocolVersion: 1`，不包含旧协议兼容层，也不实现标准 MCP 外部接口。