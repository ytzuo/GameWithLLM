#!/usr/bin/env node
/**
 * Unity JSON-RPC WebSocket 测试脚本
 *
 * 用法:
 *   node test_mcp.js                # 连接本机已启动的服务
 *   node test_mcp.js --start-server # 自动 go run ./cmd/server 启动并测试
 *
 * 该脚本使用原生 Node.js API，无第三方依赖。
 */

const crypto = require("crypto");
const net = require("net");
const path = require("path");
const { spawn } = require("child_process");

const BASE_URL = process.env.MCP_BASE_URL || "http://127.0.0.1:8080";
const WS_URL =
  process.env.UNITY_JSONRPC_WS_URL ||
  BASE_URL.replace(/^http:/, "ws:").replace(/^https:/, "wss:");
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

// RawWebSocketClient 是测试用的最小 WebSocket 客户端。
class RawWebSocketClient {
  // constructor 初始化连接参数和接收缓冲区。
  constructor(endpoint) {
    this.endpoint = endpoint;
    this.socket = null;
    this.buffer = Buffer.alloc(0);
    this.messages = [];
    this.waiters = [];
  }

  // connect 建立 TCP 连接并完成 WebSocket 握手。
  async connect() {
    const url = new URL(this.endpoint);
    const port = Number(url.port || (url.protocol === "wss:" ? 443 : 80));
    if (url.protocol !== "ws:") {
      throw new Error("测试脚本的轻量 WebSocket 客户端仅支持 ws://");
    }

    this.socket = net.createConnection({ host: url.hostname, port });
    await new Promise((resolve, reject) => {
      this.socket.once("connect", resolve);
      this.socket.once("error", reject);
    });

    const key = crypto.randomBytes(16).toString("base64");
    const path = `${url.pathname || "/"}${url.search || ""}`;
    const request = [
      `GET ${path} HTTP/1.1`,
      `Host: ${url.host}`,
      "Upgrade: websocket",
      "Connection: Upgrade",
      `Sec-WebSocket-Key: ${key}`,
      "Sec-WebSocket-Version: 13",
      "",
      "",
    ].join("\r\n");
    this.socket.write(request);

    // HTTP 升级完成后，连接里剩余的数据都按 WebSocket 帧处理。
    const leftover = await this.readHandshake();
    this.socket.on("data", (chunk) => this.handleData(chunk));
    this.socket.on("close", () => this.rejectWaiters(new Error("WebSocket closed")));
    this.socket.on("error", (err) => this.rejectWaiters(err));
    if (leftover.length > 0) {
      this.handleData(leftover);
    }
  }

  // readHandshake 读取并校验服务端的 HTTP 101 升级响应。
  readHandshake() {
    return new Promise((resolve, reject) => {
      let data = Buffer.alloc(0);
      const onData = (chunk) => {
        data = Buffer.concat([data, chunk]);
        const idx = data.indexOf("\r\n\r\n");
        if (idx < 0) return;

        this.socket.off("data", onData);
        this.socket.off("error", onError);

        const header = data.slice(0, idx).toString("utf8");
        if (!header.startsWith("HTTP/1.1 101") && !header.startsWith("HTTP/1.0 101")) {
          reject(new Error(`WebSocket 握手失败: ${header.split("\r\n")[0]}`));
          return;
        }
        resolve(data.slice(idx + 4));
      };
      const onError = (err) => {
        this.socket.off("data", onData);
        reject(err);
      };
      this.socket.on("data", onData);
      this.socket.once("error", onError);
    });
  }

  // sendJSON 将对象序列化后作为 WebSocket 文本帧发送。
  sendJSON(value) {
    const payload = Buffer.from(JSON.stringify(value), "utf8");
    this.socket.write(this.makeClientTextFrame(payload));
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
      const timer = setTimeout(() => {
        this.waiters = this.waiters.filter((w) => w.resolve !== resolve);
        reject(new Error("WebSocket read timeout"));
      }, timeoutMs);
      this.waiters.push({
        resolve: (message) => {
          clearTimeout(timer);
          resolve(message);
        },
        reject: (err) => {
          clearTimeout(timer);
          reject(err);
        },
      });
    });
  }

  // handleData 累积 TCP 数据并解析其中的 WebSocket 帧。
  handleData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    while (this.buffer.length >= 2) {
      const first = this.buffer[0];
      const second = this.buffer[1];
      const opcode = first & 0x0f;
      let len = second & 0x7f;
      let offset = 2;

      if (len === 126) {
        if (this.buffer.length < offset + 2) return;
        len = this.buffer.readUInt16BE(offset);
        offset += 2;
      } else if (len === 127) {
        if (this.buffer.length < offset + 8) return;
        const high = this.buffer.readUInt32BE(offset);
        const low = this.buffer.readUInt32BE(offset + 4);
        if (high !== 0) throw new Error("WebSocket frame too large for test client");
        len = low;
        offset += 8;
      }

      const masked = (second & 0x80) !== 0;
      let mask;
      if (masked) {
        if (this.buffer.length < offset + 4) return;
        mask = this.buffer.slice(offset, offset + 4);
        offset += 4;
      }

      if (this.buffer.length < offset + len) return;
      let payload = this.buffer.slice(offset, offset + len);
      this.buffer = this.buffer.slice(offset + len);

      if (masked) {
        // 测试客户端发给服务端的帧需要 mask；服务端返回的帧通常不带 mask。
        payload = Buffer.from(payload.map((b, i) => b ^ mask[i % 4]));
      }

      if (opcode === 0x8) {
        this.close();
        return;
      }
      if (opcode !== 0x1) {
        continue;
      }
      this.deliver(payload.toString("utf8"));
    }
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

  // makeClientTextFrame 将 payload 编码为客户端到服务端的 masked 文本帧。
  makeClientTextFrame(payload) {
    let header;
    if (payload.length < 126) {
      header = Buffer.alloc(2);
      header[0] = 0x81;
      header[1] = 0x80 | payload.length;
    } else if (payload.length < 65536) {
      header = Buffer.alloc(4);
      header[0] = 0x81;
      header[1] = 0x80 | 126;
      header.writeUInt16BE(payload.length, 2);
    } else {
      header = Buffer.alloc(10);
      header[0] = 0x81;
      header[1] = 0x80 | 127;
      header.writeUInt32BE(0, 2);
      header.writeUInt32BE(payload.length, 6);
    }

    const mask = crypto.randomBytes(4);
    // 浏览器和 WebSocket 客户端发给服务端的帧必须带 mask。
    const masked = Buffer.alloc(payload.length);
    for (let i = 0; i < payload.length; i++) {
      masked[i] = payload[i] ^ mask[i % 4];
    }
    return Buffer.concat([header, mask, masked]);
  }

  // close 关闭测试客户端连接。
  close() {
    if (this.socket) {
      this.socket.end();
      this.socket.destroy();
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
  const ws = new RawWebSocketClient(WS_URL);
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
