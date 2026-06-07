# MCP宿主服务器与Unity交互机制

本文档说明本 MCP 宿主服务器（GameMCPServer）如何与 Unity 游戏客户端进行交互，包括当前已实现的通信链路、数据流向、以及 Unity 侧需要配合实现的接入方案。

---

## 1. 总体定位

在本系统中，MCP 宿主服务器是一个**独立的 Go 进程**，职责是：

- 向 MCP 客户端线程（如大模型对话框架）声明可用的游戏工具（Tools）
- 接收来自 MCP 客户端的工具调用请求
- 将工具调用**转发给 Unity 游戏客户端**执行
- 等待 Unity 返回执行结果，再包装为 MCP 工具结果回传

Unity 游戏客户端是真正的**命令执行方**：

- 连接 MCP 宿主服务器
- 接收 NPC 行为命令
- 在 Unity 主线程中执行具体行为（移动、说话、播放动画等）
- 将执行结果返回给 MCP 宿主服务器

> **核心原则**：MCP 宿主服务器不直接操作 Unity 对象，只通过协议向 Unity 发送命令；所有游戏逻辑仍由 Unity 侧执行。

---

## 2. 当前已实现的通信链路

### 2.1 对外接口：MCP over SSE

服务器基于 [mcp-go](https://github.com/mark3labs/mcp-go) 库实现，使用 **SSE（Server-Sent Events）+ HTTP POST** 作为 MCP 协议传输层：

| 端点 | 方法 | 作用 |
|------|------|------|
| `/sse` | GET | MCP 客户端建立 SSE 长连接，接收服务器推送 |
| `/message` | POST | MCP 客户端发送 JSON-RPC 请求 |
| `/health` | GET | 健康检查 |

```
MCP客户端线程  ──SSE──►  MCP宿主服务器(:8888)
               ◄──SSE───
```

SSE 连接建立后，MCP 客户端通过 POST `/message` 发送请求，服务器通过 SSE 流推送响应。完整的 MCP 生命周期（initialize → tools/list → tools/call）均在此链路上完成。

### 2.2 当前工具列表

服务器已注册 4 个基础工具：

| 工具名 | 类型 | 说明 |
|--------|------|------|
| `get_npc_status` | 查询类 | 获取指定 NPC 的状态信息 |
| `get_npc_position` | 查询类 | 获取指定 NPC 的位置坐标 |
| `move_to` | 行为类 | 让 NPC 移动到目标位置 |
| `say` | 行为类 | 让 NPC 说一句话 |

工具处理入口位于 `internal/tool/npc.go`，目前返回**模拟数据**（Mock），尚未接入真实的 Unity 通信。

---

## 3. 与 Unity 交互的完整数据流

当大模型决定调用工具时，完整的跨进程数据流如下：

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│   远程大模型     │     │ MCP客户端线程    │     │ MCP宿主服务器    │     │  Unity游戏客户端 │
└────────┬────────┘     └────────┬────────┘     └────────┬────────┘     └────────┬────────┘
         │                       │                       │                       │
         │  返回 tool_calls      │                       │                       │
         │◄──────────────────────│                       │                       │
         │                       │                       │                       │
         │                       │  POST /message        │                       │
         │                       │  (tools/call)         │                       │
         │                       │──────────────────────►│                       │
         │                       │                       │                       │
         │                       │                       │  校验工具、参数、npc_id  │
         │                       │                       │                       │
         │                       │                       │  封装为 Unity 命令      │
         │                       │                       │                       │
         │                       │                       │  WebSocket / TCP 发送   │
         │                       │                       │──────────────────────►│
         │                       │                       │                       │
         │                       │                       │                       │ 投递到主线程队列
         │                       │                       │                       │
         │                       │                       │                       │ 执行 NPC 行为
         │                       │                       │                       │
         │                       │                       │  返回执行结果           │
         │                       │                       │◄──────────────────────│
         │                       │                       │                       │
         │                       │  SSE 推送 tool result │                       │
         │                       │◄──────────────────────│                       │
         │                       │                       │                       │
         │  提交 tool result     │                       │                       │
         │◄──────────────────────│                       │                       │
         │                       │                       │                       │
         │  生成最终自然语言回复  │                       │                       │
         │──────────────────────►│                       │                       │
         │                       │                       │                       │
```

### 3.1 阶段拆解

**① MCP 客户端发起工具调用**

MCP 客户端通过 SSE 的 POST `/message` 发送如下 JSON-RPC 请求：

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "move_to",
    "arguments": {
      "npc_id": "Ryan_001",
      "target": "城门"
    }
  }
}
```

**② 服务器校验与绑定**

`internal/tool/npc.go` 中的处理函数接收请求：

1. 提取并校验必填参数（`npc_id`、`target` 等）
2. 记录日志（当前为 `hlog.Infof`）
3. **TODO**：此处应将调用转发给 Unity

**③ 转发给 Unity（待实现）**

当前代码中标记为 TODO 的位置：

```go
// TODO: 实现实际的 NPC 移动逻辑（转发给 Unity）
return mcp.NewToolResultText(fmt.Sprintf("NPC %s 正在移动到 %s", npcID, target)), nil
```

实现后，此处应：

1. 将 MCP 工具名与参数封装为 Unity 命令对象
2. 通过 WebSocket / TCP 连接发送给 Unity
3. 等待 Unity 返回执行结果（带超时控制）
4. 将 Unity 结果包装为 `*mcp.CallToolResult`

**④ Unity 执行并返回**

Unity 侧收到命令后：

1. 网络线程仅负责收发消息，**不直接操作游戏对象**
2. 将命令投递到 Unity 主线程的任务队列
3. 主线程根据 NPC 状态机（FSM）决定何时执行
4. 执行完成后，将结果原路返回给 MCP 宿主服务器

**⑤ 服务器返回 MCP 工具结果**

服务器通过 SSE 流将结果推送给 MCP 客户端：

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      { "type": "text", "text": "[Unity 反馈]: Ryan_001 移动完成，已到达城门" }
    ],
    "isError": false
  }
}
```

---

## 4. Unity 接入方案设计

### 4.1 推荐通信方式：WebSocket 或 TCP

在独立进程架构下，MCP 宿主服务器与 Unity 之间需要**双向、长连接**的通信通道。推荐方案：

| 方案 | 优点 | 缺点 | 适用场景 |
|------|------|------|----------|
| **WebSocket** | 基于 HTTP 握手，易穿透防火墙；协议简单；C# 库成熟 | 相比 TCP 有少量帧头开销 | **推荐**，大多数 Unity 项目首选 |
| **TCP Socket** | 开销最低；完全可控 | 需要自定义协议帧格式；处理粘包/拆包 | 对延迟极度敏感的场景 |
| **gRPC** | 强类型；代码生成 | 引入较重依赖；Unity 支持较复杂 | 大型团队、强类型偏好 |

### 4.2 建议的 Unity 侧架构

在 Unity 项目中建议增加一个专用模块负责与 MCP 宿主服务器通信：

```
Unity游戏客户端
├── McpConnectionManager      // 维护与服务器的 WebSocket/TCP 连接
├── McpCommandDispatcher      // 将收到的命令分发到对应 NPC
├── McpResultCollector        // 收集 NPC 行为执行结果并回传
└── NPC 行为层
    ├── NPC_A 的 ConcurrentQueue<Command>  // 每个 NPC 一个线程安全队列
    ├── NPC_B 的 ConcurrentQueue<Command>
    └── ...
```

**关键工程约束**：

1. **网络线程不碰主线程**：WebSocket/TCP 的接收线程只负责将命令入队，绝不调用 `Transform.position` 等 Unity API
2. **主线程消费**：在 `Update` 或协程中，由各 NPC 从自身队列取出命令并执行
3. **异步消纳**：移动等耗时行为使用 Unity 的导航（NavMesh）或动画系统异步完成，完成后回调回传结果
4. **请求-响应匹配**：使用 `commandId` 或 `requestId` 保证 MCP 工具调用与 Unity 执行结果一一对应

### 4.3 建议的命令协议格式

MCP 宿主服务器向 Unity 发送的命令建议统一为如下 JSON 结构：

```json
{
  "command_id": "uuid-1234",
  "npc_id": "Ryan_001",
  "tool_name": "move_to",
  "parameters": {
    "target": "城门"
  },
  "timestamp": 1717760000000
}
```

Unity 返回的执行结果：

```json
{
  "command_id": "uuid-1234",
  "npc_id": "Ryan_001",
  "tool_name": "move_to",
  "success": true,
  "message": "移动完成，已到达城门",
  "data": {
    "position": [120.5, 0.0, 300.2]
  }
}
```

> `command_id` 是请求-响应匹配的关键，由 MCP 宿主服务器生成，Unity 原样带回。

---

## 5. NPC 身份绑定机制

### 5.1 设计原则：对大模型隐藏 `npc_id`

大模型不应该直接感知或操作 `npc_id`，它只需要理解"**当前对话的 NPC** 要执行某个行为"。`npc_id` 的绑定在 MCP 客户端线程或 MCP 宿主服务器层完成。

### 5.2 当前实现方式

当前代码中，`npc_id` 作为工具的必填参数暴露给 MCP 客户端：

```go
mcp.NewTool("move_to",
    mcp.WithDescription("让指定 NPC 移动到目标位置"),
    mcp.WithString("npc_id", mcp.Required(), mcp.Description("NPC 的唯一标识符")),
    mcp.WithString("target", mcp.Required(), mcp.Description("目标位置或地标名称")),
)
```

这种设计在**开发调试阶段**便于直接测试。在**生产阶段**，建议由 MCP 客户端线程在调用工具时自动注入当前会话绑定的 `npc_id`，大模型侧仅生成 `target`、`content` 等业务参数。

### 5.3 绑定时机

```
用户向 NPC Ryan 提问
        ↓
MCP客户端线程建立/复用 Ryan 的会话
        ↓
大模型返回 tool_calls（不含 npc_id）
        ↓
MCP客户端线程调用工具时注入 npc_id=Ryan_001
        ↓
MCP宿主服务器校验 npc_id 合法性
        ↓
转发给 Unity 执行
```

---

## 6. 当前代码中的 TODO 与实现路径

### 6.1 待实现点

`internal/tool/npc.go` 中所有工具处理函数均标记了 TODO：

| 函数 | 当前行为 | 待实现 |
|------|----------|--------|
| `HandleGetNPCStatus` | 返回 Mock 状态文本 | 查询 Unity 中 NPC 实时状态 |
| `HandleGetNPCPosition` | 返回 Mock 坐标文本 | 查询 Unity 中 NPC 实时坐标 |
| `HandleMoveTo` | 返回 Mock 移动文本 | 向 Unity 发送移动命令并等待结果 |
| `HandleSay` | 返回 Mock 说话文本 | 向 Unity 发送说话命令并等待结果 |

### 6.2 建议的增量实现步骤

**步骤 1：建立 Unity 通信层**

在 `internal/` 下新增 `unity/` 包：

```
internal/unity/
├── client.go        // WebSocket/TCP 客户端，管理连接
├── command.go       // 命令封装结构
├── result.go        // 结果解析结构
└── bridge.go        // 发送命令、等待响应、超时处理
```

**步骤 2：改造工具处理函数**

在 `internal/tool/npc.go` 中注入 `UnityBridge`：

```go
type NPCHandler struct {
    unityBridge *unity.Bridge
}

func (h *NPCHandler) HandleMoveTo(ctx context.Context, request mcp.CallToolRequest) (*mcp.CallToolResult, error) {
    // ... 参数提取 ...
    result, err := h.unityBridge.SendCommand(ctx, &unity.Command{
        NpcID:     npcID,
        ToolName:  "move_to",
        Parameters: map[string]interface{}{"target": target},
    })
    if err != nil {
        return mcp.NewToolResultText(fmt.Sprintf("执行失败: %v", err)), nil
    }
    return mcp.NewToolResultText(result.Message), nil
}
```

**步骤 3：Unity 侧实现 McpConnectionManager**

在 Unity 中新增一个常驻 `MonoBehaviour`，负责：

- 连接 MCP 宿主服务器的 WebSocket 端口
- 心跳保活
- 接收命令并分发到 NPC 队列
- 收集结果并回传

**步骤 4：端到端联调**

使用 `test_mcp.js` 脚本验证完整链路：

```bash
node test_mcp.js --start-server
```

确保从 MCP 客户端 → 服务器 → Unity → 服务器 → MCP 客户端的闭环跑通。

---

## 7. 异常处理与边界情况

MCP 宿主服务器与 Unity 交互时需要考虑的异常：

| 异常场景 | 处理策略 |
|----------|----------|
| Unity 未连接 | 向 MCP 客户端返回错误：`Unity 客户端未连接` |
| Unity 执行超时 | 设置合理超时（如 10s），超时后返回错误 |
| Unity 返回失败 | 将 Unity 的失败信息包装为 MCP tool result，设置 `isError` |
| NPC 不存在 | 校验 `npc_id`，不存在时直接拒绝 |
| 同一 NPC 行为冲突 | 查询类工具并发允许；行为类工具建议排队或拒绝重叠请求 |
| 网络断开 | 自动重连；断连期间的请求返回错误 |

---

## 8. 总结

本 MCP 宿主服务器当前已实现：

- ✅ 基于 SSE 的 MCP 协议对外服务
- ✅ 4 个基础游戏工具的声明与接收
- ✅ 工具参数校验与 NPC 身份识别
- ✅ 健康检查与测试脚本

待与 Unity 配合实现：

- ⬜ Unity 通信层（WebSocket / TCP）
- ⬜ 工具调用到 Unity 命令的转发
- ⬜ Unity 执行结果的接收与回传
- ⬜ Unity 侧的命令分发与主线程执行

MCP 宿主服务器与 Unity 的交互本质上是一个**"请求转发 + 结果回传"**的网关模式。服务器专注于协议转换、参数校验和请求追踪，Unity 专注于游戏行为的实际执行。双方通过稳定的网络通道和明确的命令协议协作，即可实现大模型对游戏 NPC 的安全、可控驱动。
