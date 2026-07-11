# MCP宿主服务器架构设计文档

## 1. 文档定位

本文档用于描述 MCP宿主服务器在独立进程部署方案下的总体架构、逻辑分层、主流程、内部模块和架构特点。

需求背景、能力边界、模块关系和第一阶段建设目标见《MCP宿主服务器需求分析文档》。

------

# 2. 总体架构设计

## 2.1 独立进程架构

本方案采用 MCP宿主服务器独立进程部署。

整体结构如下：

```text
┌──────────────────────┐
│      远程大模型       │
└──────────▲───────────┘
           │
           │ 大模型请求 / tool_calls / 工具结果
           │
┌──────────▼───────────┐
│    MCP客户端线程      │
│  管理 NPC 会话与模型交互 │
└──────────▲───────────┘
           │
           │ MCP tool 调用
           │
┌──────────▼───────────┐
│    MCP宿主服务器      │
│  工具声明 / 命令转发 │
└──────────▲───────────┘
           │
           │ WebSocket / TCP
           │
┌──────────▼───────────┐
│    Unity游戏客户端    │
│  接收命令 / 主线程执行行为 │
└──────────────────────┘
```

------

## 2.2 逻辑分层

整个系统可以分为四层。

```text
第一层：大模型推理层
第二层：NPC 会话管理层
第三层：MCP 工具宿主层
第四层：Unity 行为执行层
```

对应关系如下：

```text
大模型推理层：
远程大模型负责理解用户意图、生成 tool_calls、生成最终回复。

NPC 会话管理层：
MCP客户端线程负责维护不同 NPC 和大模型之间的会话。

MCP 工具宿主层：
MCP宿主服务器负责工具声明、工具调用、命令转发。

Unity 行为执行层：
Unity游戏客户端负责执行 NPC 具体行为，并返回结果。
```

------

## 2.3 主流程

完整主流程如下：

```text
1. 用户在 Unity 中向某个 NPC 提问。
2. Unity 将用户输入和 NPC 上下文交给 MCP客户端线程。
3. MCP客户端线程根据 npc_id 找到对应的大模型会话。
4. MCP客户端线程向远程大模型发送用户输入。
5. 远程大模型判断需要调用工具，返回 tool_calls。
6. MCP客户端线程调用 MCP宿主服务器中的对应工具。
7. MCP宿主服务器接收工具调用。
8. MCP宿主服务器校验工具和参数。
9. MCP宿主服务器绑定当前 npc_id。
10. MCP宿主服务器将 MCP 工具调用直接转发给 Unity。
11. MCP宿主服务器通过 WebSocket/TCP 将命令发送给 Unity 游戏客户端。
12. Unity 游戏客户端收到命令后投递到 Unity 主线程。
13. Unity 主线程执行 NPC 行为。
14. Unity 游戏客户端将执行结果返回给 MCP宿主服务器。
15. MCP宿主服务器将 Unity 结果转换为 MCP tool result。
16. MCP客户端线程将 tool result 返回给远程大模型。
17. 远程大模型生成最终自然语言回复。
18. MCP客户端线程将回复交给 Unity。
19. Unity 展示 NPC 回复给用户。
```

------

# 3. MCP宿主服务器内部架构

MCP宿主服务器内部建议拆分为以下模块：

```text
MCPHostServer
├── ToolRegistry
├── ToolCallHandler
├── NpcContextBinder
├── UnityCommandAdapter
├── UnityConnectionManager
├── UnityCommandBridge
├── ResultAdapter
├── RequestTracker
└── ErrorHandler
```

------

## 3.1 ToolRegistry

负责管理 MCP tools。

主要职责：

```text
注册工具
维护工具定义
声明工具列表
限制可调用工具范围
维护工具与 Unity 方法名的对应关系
```

------

## 3.2 ToolCallHandler

负责处理 MCP 工具调用。

主要职责：

```text
接收工具调用
查找工具定义
校验工具参数
调用 NPC 绑定模块
调用命令封装模块
调用 Unity 转发模块
返回工具调用结果
```

------

## 3.3 NpcContextBinder

负责绑定 NPC 上下文。

主要职责：

```text
根据会话上下文获取 npc_id
校验 npc_id 是否存在
校验当前请求是否允许控制该 NPC
将 npc_id 注入 Unity 命令
```

------

## 3.4 UnityCommandAdapter

负责将 MCP 工具调用封装为 Unity 可执行的命令。

主要职责：

```text
工具名与 Unity 方法名严格对应，不转换
封装参数结构
补充 npc_id
生成统一的 Unity 命令对象
```

------

## 3.5 UnityConnectionManager

负责维护 MCP宿主服务器与 Unity 游戏客户端之间的连接。

主要职责：

```text
监听 Unity 客户端连接
维护连接状态
处理连接断开
处理重连
支持未来多个 Unity 客户端连接
```

------

## 3.6 UnityCommandBridge

负责向 Unity 发送命令并接收结果。

主要职责：

```text
发送 Unity 命令
接收 Unity 执行结果
根据 commandId 匹配请求
处理命令发送失败
处理 Unity 执行超时
```

------

## 3.7 ResultAdapter

负责转换执行结果。

主要职责：

```text
将 Unity 成功结果转换为 MCP tool result
将 Unity 失败结果转换为 MCP tool error
过滤 Unity 内部不需要暴露的信息
统一返回格式
```

------

## 3.8 RequestTracker

负责追踪正在执行的请求。

主要职责：

```text
记录 requestId 和 commandId 的对应关系
记录请求开始时间
等待 Unity 返回结果
处理超时请求
释放已完成请求资源
```

------

## 3.9 ErrorHandler

负责错误处理。

主要职责：

```text
记录异常原因
输出统一错误信息
```

------

# 4. 方案的架构特点

采用独立进程部署后，MCP宿主服务器具有以下特点。

## 4.1 优点

```text
1. 职责更清晰
2. MCP 工具逻辑和 Unity 游戏逻辑解耦
3. 方便独立开发、测试和调试
4. 方便后续扩展更多工具
5. 方便未来支持多个 Unity 客户端
6. 可以独立监控工具调用状态
7. Unity 崩溃或重启时，MCP 服务可以独立管理连接恢复
```

------

## 4.2 缺点

```text
1. 部署复杂度更高
2. 需要处理网络通信
3. 需要处理 Unity 连接断开和重连
4. 需要设计请求和响应匹配机制
5. 相比进程内调用会有额外通信延迟
```

------

## 4.3 适用场景

独立进程方案适合：

```text
1. NPC 工具能力较多
2. 后续系统需要长期扩展
3. MCP 工具服务希望独立维护
4. Unity 客户端和工具服务需要解耦
5. 未来可能支持多个游戏客户端或多个场景
6. 需要更清晰的监控和调试能力
```
