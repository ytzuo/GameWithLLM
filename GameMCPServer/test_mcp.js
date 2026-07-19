#!/usr/bin/env node
/**
 * Unity JSON-RPC WebSocket 测试脚本
 *
 * 用法:
 *   node test_mcp.js                # 连接本机已启动的服务
 *   node test_mcp.js --start-server # 自动 go run ./cmd/server 启动并测试
 *
 * 该脚本使用 Node.js 22+ 内置的标准 WebSocket API，无第三方依赖。
 */

const path = require("path");
const { spawn } = require("child_process");

const BASE_URL = process.env.MCP_BASE_URL || "http://127.0.0.1:8080";
const WS_URL =
  process.env.UNITY_JSONRPC_WS_URL ||
  `${BASE_URL.replace(/^http:/, "ws:").replace(/^https:/, "wss:")}/unity/ws`;
const TIMEOUT_MS = 30000;

// sleep 等待指定毫秒数。
function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// fetchHealth 请求服务健康检查接口。
async function fetchHealth() {
  const res = await fetch(`${BASE_URL}/health`, { signal: AbortSignal.timeout(2000) });
  return res.ok;
}

// waitForServer 轮询等待服务启动完成。
async function waitForServer(maxMs = 30000) {
  const deadline = Date.now() + maxMs;
  while (Date.now() < deadline) {
    try {
      if (await fetchHealth()) return true;
    } catch {}
    await sleep(300);
  }
  return false;
}

// startServer 启动本地 Go 服务进程。
function startServer() {
  console.log("[启动] go run ./cmd/server");
  const proc = spawn("go", ["run", "./cmd/server"], {
    stdio: ["ignore", "pipe", "pipe"],
    shell: false,
    windowsHide: true,
    env: { ...process.env, GOCACHE: process.env.GOCACHE || path.join(__dirname, "..", ".cache", "go-build") },
  });

  proc.stdout.on("data", (d) => process.stdout.write(`[server-out] ${d}`));
  proc.stderr.on("data", (d) => process.stderr.write(`[server-err] ${d}`));
  return proc;
}

// JSONWebSocketClient 使用 Node.js 内置标准 WebSocket 实现测试协议。
class JSONWebSocketClient {
  constructor(endpoint) {
    this.endpoint = endpoint;
    this.socket = null;
    this.messages = [];
    this.waiters = [];
  }

  // connect 建立标准 WebSocket 连接并注册消息处理。
  connect() {
    return new Promise((resolve, reject) => {
      this.socket = new WebSocket(this.endpoint);
      this.socket.addEventListener("open", resolve, { once: true });
      this.socket.addEventListener("error", (event) => {
        const err = event.error || new Error("WebSocket connection failed");
        this.rejectWaiters(err);
        reject(err);
      }, { once: true });
      this.socket.addEventListener("message", (event) => {
        const message = typeof event.data === "string"
          ? event.data
          : Buffer.from(event.data).toString("utf8");
        this.deliver(message);
      });
      this.socket.addEventListener("close", () => {
        this.rejectWaiters(new Error("WebSocket closed"));
      });
    });
  }

  // sendJSON 使用标准客户端发送 JSON 文本消息。
  sendJSON(value) {
    this.socket.send(JSON.stringify(value));
  }

  // readJSON 读取一条文本消息并解析为 JSON。
  async readJSON(timeoutMs = 5000) {
    const text = await this.readText(timeoutMs);
    return JSON.parse(text);
  }

  // readText 等待下一条 WebSocket 文本消息。
  readText(timeoutMs) {
    if (this.messages.length > 0) {
      return Promise.resolve(this.messages.shift());
    }
    return new Promise((resolve, reject) => {
      const waiter = {
        resolve: (message) => {
          clearTimeout(timer);
          resolve(message);
        },
        reject: (err) => {
          clearTimeout(timer);
          reject(err);
        },
      };
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((candidate) => candidate !== waiter);
        reject(new Error("WebSocket read timeout"));
      }, timeoutMs);
      this.waiters.push(waiter);
    });
  }

  // deliver 将消息交给等待者，或暂存在消息队列中。
  deliver(message) {
    const waiter = this.waiters.shift();
    if (waiter) {
      waiter.resolve(message);
      return;
    }
    this.messages.push(message);
  }

  // rejectWaiters 在连接异常时拒绝所有等待中的读取。
  rejectWaiters(err) {
    const waiters = this.waiters.splice(0);
    for (const waiter of waiters) {
      waiter.reject(err);
    }
  }

  // close 关闭测试客户端连接。
  close() {
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.close(1000, "test complete");
    }
  }
}

let passed = 0;
let failed = 0;

// assert 记录单个测试断言的通过或失败。
function assert(condition, message) {
  if (condition) {
    passed++;
    console.log(`  OK ${message}`);
  } else {
    failed++;
    console.log(`  FAIL ${message}`);
  }
}

// runUnityProtocolTests 验证 Unity JSON-RPC WebSocket 主流程。
async function runUnityProtocolTests() {
  const ws = new JSONWebSocketClient(WS_URL);
  await ws.connect();
  try {
    // 模拟 Unity 客户端发起工具发现请求。
    ws.sendJSON({ jsonrpc: "2.0", id: "list_1", method: "tools/list" });
    const listRes = await ws.readJSON();
    const tools = listRes?.result?.tools || [];
    const toolNames = tools.map((tool) => tool.name);
    console.log("     tools:", toolNames.join(", "));
    assert(tools.length === 1, "tools/list 只返回 Unity 客户端标准工具");
    assert(toolNames.includes("game_npc_move"), "tools/list 返回 game_npc_move");

    const moveTool = tools.find((tool) => tool.name === "game_npc_move");
    assert(moveTool?.inputSchema?.required?.includes("targetLandmark"), "game_npc_move 要求 targetLandmark");
    assert(!JSON.stringify(moveTool?.inputSchema || {}).includes("npc"), "工具参数 schema 不暴露 npcId");

    // 模拟客户端请求服务端把工具调用路由给目标 NPC。
    ws.sendJSON({
      jsonrpc: "2.0",
      id: "call_1",
      method: "tools/call",
      params: {
        npcId: "Ryan_001",
        name: "game_npc_move",
        arguments: JSON.stringify({ targetLandmark: "warehouse" }),
      },
    });

    const forwarded = await ws.readJSON();
    assert(forwarded.jsonrpc === "2.0", "转发请求保留 JSON-RPC 版本");
    assert(forwarded.method === "tools/call", "服务器按 Unity 标准发出 tools/call");
    assert(forwarded.id === "call_1", "转发请求保留事务 id");
    assert(forwarded.params?.npcId === "Ryan_001", "转发请求包含 npcId");
    assert(forwarded.params?.name === "game_npc_move", "转发请求包含工具名");
    assert(forwarded.params?.arguments === JSON.stringify({ targetLandmark: "warehouse" }), "转发请求保留 arguments");

    // 模拟 Unity 完成 NPC 动作，并返回 MCP 风格的 content 结果。
    ws.sendJSON({
      jsonrpc: "2.0",
      id: "call_1",
      result: {
        content: [{ type: "text", text: "NPC开始移动" }],
        isError: false,
      },
    });

    const callRes = await ws.readJSON();
    const resultText = callRes?.result?.content?.[0]?.text || "";
    assert(callRes.id === "call_1", "服务器返回原 tools/call 响应 id");
    assert(resultText === "NPC开始移动", "服务器返回 Unity 工具执行结果");

    ws.sendJSON({
      jsonrpc: "2.0",
      id: "bad_1",
      method: "tools/call",
      params: {
        name: "game_npc_move",
        arguments: JSON.stringify({ targetLandmark: "gate" }),
      },
    });
    const errRes = await ws.readJSON();
    assert(errRes.error?.code === -32602, "缺少 npcId 时返回参数错误");
  } finally {
    ws.close();
  }
}

// runTests 准备服务进程并执行全部测试。
async function runTests() {
  const startServerFlag = process.argv.includes("--start-server");
  let serverProc = null;

  if (startServerFlag) {
    serverProc = startServer();
    console.log("[等待] 服务启动中...");
    const ok = await waitForServer(30000);
    if (!ok) {
      console.error("服务未能在 30s 内启动");
      serverProc?.kill();
      process.exit(1);
    }
  } else {
    try {
      if (!(await fetchHealth())) {
        console.error(`无法连接到 ${BASE_URL}/health，请先用 "go run ./cmd/server" 启动服务，或加上 --start-server 参数`);
        process.exit(1);
      }
    } catch (err) {
      console.error(`无法连接到 ${BASE_URL}/health: ${err.message}`);
      console.error('请先用 "go run ./cmd/server" 启动服务，或加上 --start-server 参数');
      process.exit(1);
    }
  }

  try {
    console.log("\n[1/1] Unity JSON-RPC WebSocket 协议...");
    await Promise.race([
      runUnityProtocolTests(),
      new Promise((_, reject) => setTimeout(() => reject(new Error("测试超时")), TIMEOUT_MS)),
    ]);
  } catch (err) {
    console.error("\n测试异常:", err.message);
    failed++;
  } finally {
    if (serverProc) {
      console.log("\n[关闭] 终止服务进程...");
      serverProc.kill();
      await sleep(500);
      if (!serverProc.killed) serverProc.kill("SIGKILL");
    }
  }

  console.log("\n==============================");
  console.log(`测试结果: 通过 ${passed} 项, 失败 ${failed} 项`);
  console.log("==============================");
  process.exit(failed > 0 ? 1 : 0);
}

runTests().catch((err) => {
  console.error("未捕获异常:", err);
  process.exit(1);
});
