#!/usr/bin/env node
/**
 * 当前 Unity Gateway + Go Agent Host 协议冒烟测试。
 *
 * 用法:
 *   node test_mcp.js
 *   node test_mcp.js --start-server
 *
 * 需要 Node.js 22+，使用内置 WebSocket API，无第三方依赖。
 */

const path = require("path");
const { spawn } = require("child_process");

const BASE_URL = process.env.AGENT_HOST_BASE_URL || "http://127.0.0.1:8080";
const WS_URL = process.env.UNITY_JSONRPC_WS_URL ||
  `${BASE_URL.replace(/^http:/, "ws:").replace(/^https:/, "wss:")}/unity/ws`;
const TIMEOUT_MS = 30000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function fetchHealth() {
  const response = await fetch(`${BASE_URL}/health`, { signal: AbortSignal.timeout(2000) });
  return response.ok;
}

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

function startServer() {
  console.log("[启动] go run ./cmd/server");
  const proc = spawn("go", ["run", "./cmd/server"], {
    stdio: ["ignore", "pipe", "pipe"],
    shell: false,
    windowsHide: true,
    cwd: __dirname,
    env: { ...process.env, GOCACHE: process.env.GOCACHE || path.join(__dirname, "..", ".cache", "go-build") },
  });
  proc.stdout.on("data", (data) => process.stdout.write(`[server-out] ${data}`));
  proc.stderr.on("data", (data) => process.stderr.write(`[server-err] ${data}`));
  return proc;
}

class JSONWebSocketClient {
  constructor(endpoint) {
    this.endpoint = endpoint;
    this.messages = [];
    this.waiters = [];
  }

  connect() {
    return new Promise((resolve, reject) => {
      this.socket = new WebSocket(this.endpoint);
      this.socket.addEventListener("open", resolve, { once: true });
      this.socket.addEventListener("error", (event) => reject(event.error || new Error("WebSocket connection failed")), { once: true });
      this.socket.addEventListener("message", (event) => {
        const text = typeof event.data === "string" ? event.data : Buffer.from(event.data).toString("utf8");
        const waiter = this.waiters.shift();
        if (waiter) waiter.resolve(text);
        else this.messages.push(text);
      });
      this.socket.addEventListener("close", () => {
        for (const waiter of this.waiters.splice(0)) waiter.reject(new Error("WebSocket closed"));
      });
    });
  }

  send(value) {
    this.socket.send(JSON.stringify(value));
  }

  async read(timeoutMs = 5000) {
    if (this.messages.length > 0) return JSON.parse(this.messages.shift());
    const text = await new Promise((resolve, reject) => {
      const waiter = { resolve: null, reject: null };
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((candidate) => candidate !== waiter);
        reject(new Error("WebSocket read timeout"));
      }, timeoutMs);
      waiter.resolve = (value) => { clearTimeout(timer); resolve(value); };
      waiter.reject = (error) => { clearTimeout(timer); reject(error); };
      this.waiters.push(waiter);
    });
    return JSON.parse(text);
  }

  close() {
    if (this.socket?.readyState === WebSocket.OPEN) this.socket.close(1000, "test complete");
  }
}

let passed = 0;
let failed = 0;
function assert(condition, message) {
  if (condition) {
    passed++;
    console.log(`  OK ${message}`);
  } else {
    failed++;
    console.log(`  FAIL ${message}`);
  }
}

async function runProtocolTests() {
  const ws = new JSONWebSocketClient(WS_URL);
  await ws.connect();
  try {
    const tools = [{
      name: "game_npc_move",
      description: "使 NPC 前往指定目标",
      inputSchema: {
        type: "object",
        properties: {
          targetId: {
            type: "string",
            enum: ["landmark:warehouse", "landmark:gate"],
          },
        },
        required: ["targetId"],
      },
    }];
    const instanceId = `e2e-game-${Date.now()}`;

    ws.send({
      jsonrpc: "2.0", id: "register-1", method: "unity.register",
      params: {
        protocolVersion: 1,
        instanceId,
        tools,
        npcs: ["Ryan_001"],
        npcTools: { Ryan_001: ["game_npc_move"] },
      },
    });
    const registered = await ws.read();
    console.log("  recv", JSON.stringify(registered));
    assert(registered.id === "register-1", "连接后首条服务端消息是注册响应");
    assert(registered?.result?.accepted === true, "unity.register 注册成功");
    assert(registered?.result?.protocolVersion === 1, "服务端确认内部协议版本 1");

    ws.send({
      jsonrpc: "2.0", id: "conversation-start-1", method: "conversation.start",
      params: { playerId: "e2e-player", npcId: "Ryan_001" },
    });
    const started = await ws.read();
    console.log("  recv", JSON.stringify(started));
    assert(Boolean(started?.result?.sessionId), "Go Agent Host 创建对话 Session");

    ws.send({
      jsonrpc: "2.0", id: "conversation-end-1", method: "conversation.end",
      params: { sessionId: started?.result?.sessionId },
    });
    const ended = await ws.read();
    console.log("  recv", JSON.stringify(ended));
    assert(ended?.result?.ok === true, "Go Agent Host 正常结束对话 Session");

  } finally {
    ws.close();
  }
}

async function runTests() {
  let serverProc = null;
  if (process.argv.includes("--start-server")) {
    serverProc = startServer();
    console.log("[等待] 服务启动中...");
    if (!(await waitForServer())) {
      serverProc.kill();
      throw new Error("服务未能在 30s 内启动");
    }
  } else if (!(await fetchHealth().catch(() => false))) {
    throw new Error(`无法连接 ${BASE_URL}/health，请先启动服务或使用 --start-server`);
  }

  try {
    console.log("\n[1/1] Unity Gateway + Go Agent Host...");
    await Promise.race([
      runProtocolTests(),
      new Promise((_, reject) => setTimeout(() => reject(new Error("测试超时")), TIMEOUT_MS)),
    ]);
  } catch (error) {
    console.error("\n测试异常:", error.message);
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

runTests().catch((error) => {
  console.error("未捕获异常:", error.message);
  process.exit(1);
});
