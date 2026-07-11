# Unity 与 Go 项目启动说明

本文档记录当前目录中两个子项目的启动方式：

- `GameMCPServer`：Go 编写的本地 MCP 宿主服务器。
- `unity-NPC-agent-client`：Unity 客户端工程。

建议启动顺序是：先启动 Go 服务端，再打开并运行 Unity 客户端。

## 一、环境要求

### Go 服务端

当前工程的 `GameMCPServer/go.mod` 声明：

```txt
go 1.26
```

本机已验证可用工具链：

```powershell
go version
```

验证结果为 `go1.26.3 windows/amd64`，并且在 `GameMCPServer` 目录执行 `go test ./...` 可以通过。

### Unity 客户端

Unity 工程目录为：

```txt
unity-NPC-agent-client
```

工程版本记录在 `ProjectSettings/ProjectVersion.txt`：

```txt
6000.3.19f1
```

建议使用 Unity Hub 安装并打开相同版本，或使用兼容的 Unity 6 编辑器版本打开该工程。

## 二、启动 Go MCP 服务端

推荐在 monorepo 根目录使用 Make：

```bash
make server
```

也可以在 PowerShell 中进入 Go 项目目录后手动启动：

```powershell
cd C:\Users\zz\Desktop\game\GameMCPServer
```

启动服务：

```powershell
go run .\cmd\server
```

启动成功后，终端会打印类似信息：

```txt
Game MCP Server starting on http://localhost:8080
Unity JSON-RPC WebSocket endpoint: ws://localhost:8080
Unity JSON-RPC WebSocket endpoint: ws://localhost:8080/ws
```

服务监听端口为 `8080`，主要入口包括：

- 健康检查：`http://localhost:8080/health`
- Unity 默认 WebSocket 地址：`ws://127.0.0.1:8080`
- 显式 WebSocket 地址：`ws://localhost:8080/ws`

可在另一个 PowerShell 窗口中验证健康检查：

```powershell
curl.exe http://localhost:8080/health
```

正常返回应包含：

```json
{"service":"GameMCPServer","status":"ok"}
```

## 三、启动 Unity 客户端

1. 打开 Unity Hub。
2. 选择“添加项目”或“Open”，打开目录：

```txt
C:\Users\zz\Desktop\game\unity-NPC-agent-client
```

3. 等待 Unity 导入依赖和编译脚本。
4. 打开场景：

```txt
Assets/Scenes/SampleScene.unity
```

该场景已在 `ProjectSettings/EditorBuildSettings.asset` 中启用，是当前工程的默认场景。

5. 确认场景中存在以下对象：

- `MCPClient`：挂载 `McpAsyncClient`，负责连接 Go MCP 服务端。
- `npcEntity1`：挂载 `NpcEntity`，负责接收和执行 NPC 工具调用。

6. 点击 Unity 编辑器顶部的 Play 按钮运行场景。

Unity 客户端会优先读取 monorepo 根目录 `.env.local` 中的连接地址：

```env
UNITY_JSONRPC_WS_URL=ws://127.0.0.1:8080
```

脚本中的默认 fallback 地址为：

```csharp
public string mcpHostWsUrl = "ws://127.0.0.1:8080";
```

因此只要 Go 服务端已经在本机 `8080` 端口启动，Unity 进入 Play 模式后会自动连接。连接成功时，Unity Console 会出现类似日志：

```txt
[MCP Client] WebSocket 连接宿主服务器成功！
```

Go 服务端终端也会出现类似日志：

```txt
Unity JSON-RPC websocket connected
```

## 四、LLM API 配置

Unity 客户端的 `McpAsyncClient` 会优先读取 monorepo 根目录 `.env.local` 中的大模型配置，代码里只保留非敏感 fallback：

```csharp
public string llmApiUrl = "https://api.openai.com/v1/chat/completions";
public string llmModel = "gpt-4o-mini";
```

如果需要实际调用大模型，在 monorepo 根目录创建 `.env.local`，并配置：

```env
OPENAI_API_KEY=你的本机 API Key
LLM_API_URL=https://api.openai.com/v1/chat/completions
LLM_MODEL=gpt-4o-mini
```

注意：不要把真实 API Key 提交到代码仓库。`.env.local` 已在根目录 `.gitignore` 中忽略。

## 五、推荐启动流程

每次本地联调可按以下顺序操作：

1. 在 monorepo 根目录启动 Go 服务端：

```bash
make server
```

2. 打开 Unity 工程：

```txt
C:\Users\zz\Desktop\game\unity-NPC-agent-client
```

3. 打开 `Assets/Scenes/SampleScene.unity`。
4. 检查根目录 `.env.local` 中的 `UNITY_JSONRPC_WS_URL` 是否为 `ws://127.0.0.1:8080`。
5. 如需调用大模型，配置 `OPENAI_API_KEY`。
6. 点击 Play。
7. 在 Unity Console 和 Go 服务端终端中确认 WebSocket 已连接。

## 六、常见问题

### Unity 提示 WebSocket 连接失败

优先检查 Go 服务端是否已启动，以及 `8080` 端口是否被占用。

可执行：

```powershell
curl.exe http://localhost:8080/health
```

如果没有返回健康检查结果，说明 Go 服务端没有正常运行。

### 端口 8080 被占用

当前 Go 服务端端口写在：

```txt
GameMCPServer/cmd/server/main.go
```

当前 Unity 客户端地址写在：

```txt
unity-NPC-agent-client/Assets/Scripts/McpClient/McpAsyncClient.cs
```

如果修改服务端端口，需要同步修改 Unity 中 `MCPClient` 的 `mcpHostWsUrl`。

### Unity 能连接服务端，但大模型请求失败

检查根目录 `.env.local` 是否配置了 `OPENAI_API_KEY`，以及当前网络环境是否可以访问 `LLM_API_URL`。

### Unity 包导入失败

`Packages/manifest.json` 中包含一个 GitHub 依赖：

```json
"com.xxhq.htmltougui": "https://github.com/jixinhaoqi/HtmlToUGUI.git"
```

如果首次打开 Unity 工程时导包失败，通常需要检查 Git 是否可用，以及当前网络是否能访问 GitHub。
