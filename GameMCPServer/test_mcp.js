#!/usr/bin/env node
/**
 * MCP HTTP+SSE 测试脚本
 *
 * 用法:
 *   node test_mcp.js                # 连接本机已启动的服务
 *   node test_mcp.js --start-server # 自动 go run main.go 启动并测试
 *
 * 该脚本使用原生 fetch (Node >= 18)，无第三方依赖。
 */

const { spawn } = require("child_process");

const BASE_URL = process.env.MCP_BASE_URL || "http://127.0.0.1:8888";
const TIMEOUT_MS = 30000;

// ---------------------------- helpers ----------------------------

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function fetchHealth() {
  const res = await fetch(`${BASE_URL}/health`, { signal: AbortSignal.timeout(2000) });
  return res.ok;
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
  console.log("[启动] go run main.go");
  const proc = spawn("go", ["run", "main.go"], {
    stdio: ["ignore", "pipe", "pipe"],
    shell: false,
    windowsHide: true,
  });

  proc.stdout.on("data", (d) => process.stdout.write(`[server-out] ${d}`));
  proc.stderr.on("data", (d) => process.stderr.write(`[server-err] ${d}`));

  proc.on("exit", (code) => {
    if (code !== null && code !== 0 && code !== 143 && code !== 9) {
      console.log(`[server] 进程退出，code=${code}`);
    }
  });

  return proc;
}

// ---------------------------- SSE client ----------------------------

class SSEClient {
  constructor(baseUrl) {
    this.baseUrl = baseUrl;
    this.messageEndpoint = null;
    this._buffer = "";
    this._events = [];
    this._onEvent = null;
    this._reader = null;
    this._connected = false;
  }

  async connect() {
    const url = `${this.baseUrl}/sse`;
    const res = await fetch(url, {
      headers: { Accept: "text/event-stream" },
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });
    if (!res.ok || !res.body) {
      throw new Error(`SSE 连接失败: ${res.status} ${res.statusText}`);
    }
    this._reader = res.body.getReader();
    this._connected = true;
    this._readLoop();

    // 等待 endpoint 事件
    const evt = await this.waitForEvent("endpoint", 10000);
    if (!evt) {
      throw new Error("未在 SSE 中收到 endpoint 事件");
    }
    const ep = evt.data.trim();
    this.messageEndpoint = ep.startsWith("http") ? ep : `${this.baseUrl}${ep}`;
    console.log(`[SSE] endpoint = ${this.messageEndpoint}`);
  }

  setOnEvent(handler) {
    this._onEvent = handler;
    // 把已经缓冲的事件也交给 handler
    while (this._events.length) {
      const e = this._events.shift();
      if (e) handler(e);
    }
  }

  async _readLoop() {
    const decoder = new TextDecoder();
    try {
      while (this._connected) {
        const { done, value } = await this._reader.read();
        if (done) break;
        this._buffer += decoder.decode(value, { stream: true });
        this._parseBuffer();
      }
    } catch (err) {
      if (this._connected) {
        // EOF 或连接关闭属于正常情况，不打印
        if (err.name !== "AbortError") {
          console.error("[SSE] 读取错误:", err.message);
        }
      }
    } finally {
      this._connected = false;
    }
  }

  _parseBuffer() {
    // SSE 标准使用双换行分隔事件：\n\n 或 \r\n\r\n（mcp-go 实际输出 event\ndata...\r\n\r\n）
    const boundary = this._buffer.includes("\r\n\r\n") ? "\r\n\r\n" : "\n\n";
    const parts = this._buffer.split(boundary);
    this._buffer = parts.pop(); // keep incomplete chunk
    for (const part of parts) {
      if (!part.trim()) continue;
      const lines = part.split(/\r?\n/);
      const evt = { event: "message", data: "" };
      const dataLines = [];
      for (const line of lines) {
        if (line.startsWith("event:")) {
          evt.event = line.slice("event:".length).trim();
        } else if (line.startsWith("data:")) {
          dataLines.push(line.slice("data:".length).trim());
        } else if (line.startsWith("id:")) {
          evt.id = line.slice("id:".length).trim();
        }
      }
      evt.data = dataLines.join("\n");
      if (this._onEvent) {
        this._onEvent(evt);
      } else {
        this._events.push(evt);
      }
    }
  }

  waitForEvent(eventName, timeoutMs = 10000) {
    return new Promise((resolve, reject) => {
      const deadline = Date.now() + timeoutMs;
      const timer = setInterval(() => {
        const idx = this._events.findIndex((e) => e.event === eventName);
        if (idx >= 0) {
          clearInterval(timer);
          resolve(this._events.splice(idx, 1)[0]);
          return;
        }
        if (Date.now() > deadline) {
          clearInterval(timer);
          reject(new Error(`等待 SSE 事件 ${eventName} 超时`));
        }
      }, 50);
    });
  }

  close() {
    this._connected = false;
    try {
      this._reader?.cancel();
    } catch {}
  }
}

// ---------------------------- MCP client ----------------------------

class MCPClient {
  constructor(baseUrl) {
    this.baseUrl = baseUrl;
    this.sse = new SSEClient(baseUrl);
    this._id = 0;
    this._pending = new Map(); // id -> {resolve, reject, timer}
  }

  async connect() {
    await this.sse.connect();
    this.sse.setOnEvent((evt) => this._handleEvent(evt));
  }

  _handleEvent(evt) {
    if (evt.event !== "message" || !evt.data) return;
    let msg;
    try {
      msg = JSON.parse(evt.data);
    } catch {
      return;
    }
    // 只处理带 id 的响应
    if (msg.id === undefined || msg.id === null) return;
    const pending = this._pending.get(msg.id);
    if (!pending) return;
    this._pending.delete(msg.id);
    clearTimeout(pending.timer);
    if (msg.error) {
      pending.reject(new Error(`JSON-RPC 错误: ${JSON.stringify(msg.error)}`));
    } else {
      pending.resolve(msg.result);
    }
  }

  async send(method, params = {}) {
    const id = ++this._id;
    const body = {
      jsonrpc: "2.0",
      id,
      method,
      params,
    };

    // MCP over SSE: 请求通过 POST /message 发送，服务器返回 202 Accepted；
    // 真实响应通过 SSE 流以 JSON-RPC 消息形式推送回来。
    const postPromise = fetch(this.sse.messageEndpoint, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });

    const responsePromise = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this._pending.delete(id);
        reject(new Error(`请求 ${method} (id=${id}) 等待 SSE 响应超时`));
      }, TIMEOUT_MS);
      this._pending.set(id, { resolve, reject, timer });
    });

    const res = await postPromise;
    if (!res.ok && res.status !== 202) {
      this._pending.delete(id);
      throw new Error(`POST ${this.sse.messageEndpoint} 失败: ${res.status}`);
    }

    return responsePromise;
  }

  async initialize() {
    return this.send("initialize", {
      protocolVersion: "2024-11-05",
      capabilities: {},
      clientInfo: { name: "js-test-client", version: "1.0.0" },
    });
  }

  async listTools() {
    return this.send("tools/list");
  }

  async callTool(name, args) {
    return this.send("tools/call", { name, arguments: args });
  }

  close() {
    for (const { reject, timer } of this._pending.values()) {
      clearTimeout(timer);
      reject(new Error("客户端关闭"));
    }
    this._pending.clear();
    this.sse.close();
  }
}

// ---------------------------- assertions ----------------------------

let passed = 0;
let failed = 0;

function assert(condition, message) {
  if (condition) {
    passed++;
    console.log(`  ✅ ${message}`);
  } else {
    failed++;
    console.log(`  ❌ ${message}`);
  }
}

// ---------------------------- main ----------------------------

async function runTests() {
  const startServerFlag = process.argv.includes("--start-server");
  let serverProc = null;

  if (startServerFlag) {
    serverProc = startServer();
    console.log("[等待] 服务启动中...");
    const ok = await waitForServer(30000);
    if (!ok) {
      console.error("❌ 服务未能在 30s 内启动");
      serverProc?.kill();
      process.exit(1);
    }
  } else {
    try {
      if (!(await fetchHealth())) {
        console.error(`❌ 无法连接到 ${BASE_URL}/health，请先用 "go run main.go" 启动服务，或加上 --start-server 参数`);
        process.exit(1);
      }
    } catch (err) {
      console.error(`❌ 无法连接到 ${BASE_URL}/health: ${err.message}`);
      console.error("   请先用 \"go run main.go\" 启动服务，或加上 --start-server 参数");
      process.exit(1);
    }
  }

  const client = new MCPClient(BASE_URL);

  try {
    console.log("\n[1/6] 连接 SSE...");
    await client.connect();
    assert(!!client.sse.messageEndpoint, "SSE endpoint 已获取");

    console.log("\n[2/6] initialize 握手...");
    const initRes = await client.initialize();
    assert(initRes?.serverInfo?.name === "GameMCPServer", `服务器名称正确: ${initRes?.serverInfo?.name}`);
    assert(initRes?.serverInfo?.version === "1.0.0", `服务器版本正确: ${initRes?.serverInfo?.version}`);
    assert(initRes?.protocolVersion === "2024-11-05", `协议版本正确: ${initRes?.protocolVersion}`);

    console.log("\n[3/6] tools/list 获取工具列表...");
    const toolsRes = await client.listTools();
    const tools = toolsRes?.tools || [];
    const toolNames = tools.map((t) => t.name).sort();
    console.log("     发现工具:", toolNames.join(", "));
    assert(toolNames.length === 4, "工具数量为 4");
    assert(
      toolNames.includes("get_npc_status") &&
      toolNames.includes("get_npc_position") &&
      toolNames.includes("move_to") &&
      toolNames.includes("say"),
      "包含全部 4 个预期工具"
    );

    const getNpcStatus = tools.find((t) => t.name === "get_npc_status");
    assert(getNpcStatus?.description?.includes("NPC"), "get_npc_status 描述包含 NPC");
    assert(getNpcStatus?.inputSchema?.required?.includes("npc_id"), "get_npc_status 要求 npc_id");

    const moveTo = tools.find((t) => t.name === "move_to");
    assert(moveTo?.inputSchema?.required?.includes("target"), "move_to 要求 target");

    const say = tools.find((t) => t.name === "say");
    assert(say?.inputSchema?.required?.includes("content"), "say 要求 content");

    console.log("\n[4/6] 调用 get_npc_status...");
    const statusRes = await client.callTool("get_npc_status", { npc_id: "npc_001" });
    const statusText = statusRes?.content?.[0]?.text || "";
    console.log("     结果:", statusText);
    assert(statusText.includes("npc_001"), "结果包含 npc_id");
    assert(statusText.includes("状态"), "结果包含'状态'关键字");

    console.log("\n[5/6] 调用 get_npc_position...");
    const posRes = await client.callTool("get_npc_position", { npc_id: "npc_002" });
    const posText = posRes?.content?.[0]?.text || "";
    console.log("     结果:", posText);
    assert(posText.includes("npc_002"), "结果包含 npc_id");
    assert(/\d/.test(posText), "结果包含数字坐标");

    console.log("\n[6/6] 调用 move_to 和 say...");
    const moveRes = await client.callTool("move_to", { npc_id: "npc_003", target: "城门" });
    const moveText = moveRes?.content?.[0]?.text || "";
    console.log("     move_to:", moveText);
    assert(moveText.includes("npc_003") && moveText.includes("城门"), "move_to 结果包含 npc_id 和 target");

    const sayRes = await client.callTool("say", { npc_id: "npc_004", content: "你好，世界！" });
    const sayText = sayRes?.content?.[0]?.text || "";
    console.log("     say    :", sayText);
    assert(sayText.includes("npc_004") && sayText.includes("你好，世界"), "say 结果包含 npc_id 和 content");

    console.log("\n[额外] 测试缺少必填参数...");
    try {
      await client.callTool("get_npc_status", {});
      assert(false, "缺少 npc_id 时应报错");
    } catch (err) {
      assert(true, `缺少 npc_id 时正确报错: ${err.message}`);
    }
  } catch (err) {
    console.error("\n❌ 测试异常:", err.message);
    failed++;
  } finally {
    client.close();
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
