# Game MCP Monorepo

本仓库是 Unity 客户端和 Go MCP 宿主服务器的轻量 monorepo。
关于go服务器，请看GameMCPServer/docs/
关于客户端，请看unity-NPC-agent-client/README.md

## 目录结构

```txt
.
├── GameMCPServer/             # Go MCP 宿主服务器
├── unity-NPC-agent-client/    # Unity 客户端
├── docs/                      # 项目文档
├── Makefile                   # 跨项目开发入口
├── go.work                    # Go workspace
└── .env.example               # 本地配置示例
```

## 环境配置

复制根目录 `.env.example` 为 `.env.local`，再填写本机配置：

```txt
.env.local
```

`.env.local` 不提交到仓库。Go 服务端和 Unity 客户端都会从 monorepo 根目录读取该文件；系统环境变量优先级更高。

主要变量：

```env
MCP_SERVER_ADDR=:8080
MCP_BASE_URL=http://127.0.0.1:8080
UNITY_JSONRPC_WS_URL=ws://127.0.0.1:8080/unity/ws
UNITY_INSTANCE_ID=local-game-1
PLAYER_ID=local-player-1
UNITY_TOOL_TIMEOUT_SECONDS=10

LLM_API_URL=https://api.openai.com/v1/chat/completions
LLM_MODEL=gpt-4o-mini
LLM_API_KEY=
LLM_REQUEST_TIMEOUT_SECONDS=60
LLM_MAX_TOOL_ROUNDS=4
```

## Make 命令

macOS 通常自带 `make`。Windows 用户可以通过 Git Bash、MSYS2、WSL 或 Chocolatey 安装 GNU Make。

```bash
make help
```

常用命令：

```bash
make env-check
make server
make test
make unity-info
```

直接运行 `GameMCPServer/test_mcp.js` 需要 Node.js 22 或更高版本；脚本使用 Node 内置标准 WebSocket，不安装额外依赖。

## 启动服务端

```bash
make server
```

默认监听：

```txt
http://127.0.0.1:8080
ws://127.0.0.1:8080/unity/ws
```

如果需要换端口，修改 `.env.local` 中的：

```env
MCP_SERVER_ADDR=:8090
MCP_BASE_URL=http://127.0.0.1:8090
UNITY_JSONRPC_WS_URL=ws://127.0.0.1:8090/unity/ws
```

## Unity 内部协议

Go 与 Unity 只使用内部执行协议 v1：Unity 连接后注册实例、NPC 和工具能力，Go 通过 `unity.tool.execute` 下发命令。旧 `tools/list` / `tools/call` 环回协议、`/ws` 和根路径 WebSocket 入口均已删除；唯一 WebSocket 地址是 `/unity/ws`。Unity 的 `UnityGatewayClient` 会拼接 WebSocket 分片消息、串行发送，并在 Go 重启后按指数退避自动重连和重新注册。

玩家消息通过 `player.message` 交给 Go Agent Host；模型调用、Session 历史和工具循环均在 Go 完成，Unity 只执行游戏行为并展示最终回复。

## 启动 Unity

使用 Unity Hub 打开：

```txt
unity-NPC-agent-client
```

当前 Unity 版本见：

```txt
unity-NPC-agent-client/ProjectSettings/ProjectVersion.txt
```

打开场景：

```txt
Assets/Scenes/SampleScene.unity
```

Unity 运行时只读取 `UNITY_JSONRPC_WS_URL`、`UNITY_INSTANCE_ID` 和 `PLAYER_ID`；`UNITY_INSTANCE_ID` 是实例前缀，运行时会追加启动 UUID。LLM 地址、模型、密钥、请求超时和最大工具轮数只由 Go Server 读取，Unity 构建物不再包含 API Key。`OPENAI_API_KEY` 仍作为 `LLM_API_KEY` 的兼容别名。
