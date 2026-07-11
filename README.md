# Game MCP Monorepo

本仓库是 Unity 客户端和 Go MCP 宿主服务器的轻量 monorepo。

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
UNITY_JSONRPC_WS_URL=ws://127.0.0.1:8080
UNITY_TOOL_TIMEOUT_SECONDS=10

LLM_API_URL=https://api.openai.com/v1/chat/completions
LLM_MODEL=gpt-4o-mini
OPENAI_API_KEY=
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

## 启动服务端

```bash
make server
```

默认监听：

```txt
http://127.0.0.1:8080
ws://127.0.0.1:8080
```

如果需要换端口，修改 `.env.local` 中的：

```env
MCP_SERVER_ADDR=:8090
MCP_BASE_URL=http://127.0.0.1:8090
UNITY_JSONRPC_WS_URL=ws://127.0.0.1:8090
```

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

Unity 运行时会读取根目录 `.env.local` 中的 `UNITY_JSONRPC_WS_URL`、`LLM_API_URL`、`LLM_MODEL` 和 `OPENAI_API_KEY`。

