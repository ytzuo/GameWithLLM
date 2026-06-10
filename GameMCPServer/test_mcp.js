#!/usr/bin/env node
/**
 * MCP Streamable HTTP 测试脚本
 *
 * 用法:
 *   node test_mcp.js                # 连接本机已启动的服务
 *   node test_mcp.js --start-server # 自动 go run ./cmd/server 启动并测试
 *
 * 该脚本使用原生 fetch (Node >= 18)，无第三方依赖。
 */

const { spawn } = require("child_process");

const BASE_URL = process.env.MCP_BASE_URL || "http://127.0.0.1:8080";
const MCP_URL = process.env.MCP_ENDPOINT || `${BASE_URL}/mcp`;
const UNITY_WS_URL =
  process.env.UNITY_WS_URL ||
  `${BASE_URL.replace(/^http:/, "ws:").replace(/^https:/, "wss:")}/unity/ws`;
const TIMEOUT_MS = 30000;

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
  console.log("[启动] go run ./cmd/server");
  const proc = spawn("go", ["run", "./cmd/server"], {
    stdio: ["ignore", "pipe", "pipe"],
    shell: false,
    windowsHide: true,
  });

  proc.stdout.on("data", (d) => process.stdout.write(`[server-out] ${d}`));
  proc.stderr.on("data", (d) => process.stderr.write(`[server-err] ${d}`));
  return proc;
}

function startMockUnity() {
  console.log(`[启动] go run ./cmd/mockunity --server ${UNITY_WS_URL}`);
  const proc = spawn("go", ["run", "./cmd/mockunity", "--server", UNITY_WS_URL], {
    stdio: ["ignore", "pipe", "pipe"],
    shell: false,
    windowsHide: true,
  });

  proc.stdout.on("data", (d) => process.stdout.write(`[mock-out] ${d}`));
  proc.stderr.on("data", (d) => process.stderr.write(`[mock-err] ${d}`));
  return proc;
}

async function fetchUnityStatus() {
  const res = await fetch(`${BASE_URL}/unity/status`, { signal: AbortSignal.timeout(2000) });
  if (!res.ok) return { connected: false };
  return res.json();
}

async function waitForUnity(maxMs = 10000) {
  const deadline = Date.now() + maxMs;
  while (Date.now() < deadline) {
    try {
      const status = await fetchUnityStatus();
      if (status.connected) return status;
    } catch {}
    await sleep(300);
  }
  return { connected: false };
}

class MCPClient {
  constructor(endpoint) {
    this.endpoint = endpoint;
    this.sessionId = "";
    this._id = 0;
  }

  async send(method, params = {}) {
    const id = ++this._id;
    const headers = {
      "Content-Type": "application/json",
      Accept: "application/json, text/event-stream",
      "MCP-Protocol-Version": "2025-06-18",
    };
    if (this.sessionId) {
      headers["Mcp-Session-Id"] = this.sessionId;
    }

    const res = await fetch(this.endpoint, {
      method: "POST",
      headers,
      body: JSON.stringify({ jsonrpc: "2.0", id, method, params }),
      signal: AbortSignal.timeout(TIMEOUT_MS),
    });

    if (!res.ok) {
      throw new Error(`POST ${this.endpoint} 失败: ${res.status} ${await res.text()}`);
    }

    const sessionId = res.headers.get("Mcp-Session-Id");
    if (sessionId) {
      this.sessionId = sessionId;
    }

    const contentType = res.headers.get("Content-Type") || "";
    if (contentType.includes("text/event-stream")) {
      return this.readSSEJSONResponse(res, id);
    }

    const msg = await res.json();
    if (msg.error) {
      throw new Error(`JSON-RPC 错误: ${JSON.stringify(msg.error)}`);
    }
    return msg.result;
  }

  async readSSEJSONResponse(res, id) {
    const text = await res.text();
    for (const part of text.split(/\r?\n\r?\n/)) {
      const data = part
        .split(/\r?\n/)
        .filter((line) => line.startsWith("data:"))
        .map((line) => line.slice("data:".length).trim())
        .join("\n");
      if (!data) continue;
      const msg = JSON.parse(data);
      if (msg.id !== id) continue;
      if (msg.error) {
        throw new Error(`JSON-RPC 错误: ${JSON.stringify(msg.error)}`);
      }
      return msg.result;
    }
    throw new Error(`未在 SSE 响应中找到 id=${id} 的 JSON-RPC 响应`);
  }

  async initialize() {
    return this.send("initialize", {
      protocolVersion: "2025-06-18",
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

  async openEventStream() {
    if (!this.sessionId) {
      throw new Error("missing session id");
    }
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 2000);
    try {
      const res = await fetch(this.endpoint, {
        method: "GET",
        headers: {
          Accept: "text/event-stream",
          "Mcp-Session-Id": this.sessionId,
          "MCP-Protocol-Version": "2025-06-18",
        },
        signal: controller.signal,
      });
      const contentType = res.headers.get("Content-Type") || "";
      const body = res.status === 200 ? "" : await res.text();
      if (res.status === 200) {
        try {
          await res.body?.cancel();
        } catch {}
      }
      return { status: res.status, contentType, body };
    } finally {
      clearTimeout(timer);
      controller.abort();
    }
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

async function runTests() {
  const startServerFlag = process.argv.includes("--start-server");
  let serverProc = null;
  let mockUnityProc = null;

  if (startServerFlag) {
    serverProc = startServer();
    console.log("[等待] 服务启动中...");
    const ok = await waitForServer(30000);
    if (!ok) {
      console.error("服务未能在 30s 内启动");
      serverProc?.kill();
      process.exit(1);
    }
    mockUnityProc = startMockUnity();
    console.log("[等待] mockUnity 连接中...");
    const unityStatus = await waitForUnity(10000);
    if (!unityStatus.connected) {
      console.error("mockUnity 未能在 10s 内连接");
      serverProc?.kill();
      mockUnityProc?.kill();
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
    const unityStatus = await waitForUnity(1000);
    if (!unityStatus.connected) {
      console.error(`无法连接到 mockUnity，请先启动: go run ./cmd/mockunity --server ${UNITY_WS_URL}`);
      process.exit(1);
    }
  }

  const client = new MCPClient(MCP_URL);

  try {
    console.log("\n[1/6] initialize 握手...");
    const initRes = await client.initialize();
    assert(initRes?.serverInfo?.name === "GameMCPServer", `服务器名称正确: ${initRes?.serverInfo?.name}`);
    assert(initRes?.serverInfo?.version === "1.0.0", `服务器版本正确: ${initRes?.serverInfo?.version}`);
    assert(!!client.sessionId, "已获取 Mcp-Session-Id");

    console.log("\n[2/6] 打开 GET /mcp event stream...");
    const streamRes = await client.openEventStream();
    assert(streamRes.status === 200, `GET /mcp 返回 200: ${streamRes.status}${streamRes.body ? ` (${streamRes.body.trim()})` : ""}`);
    assert(streamRes.contentType.includes("text/event-stream"), `GET /mcp 返回 event-stream: ${streamRes.contentType}`);

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
    assert(getNpcStatus?.inputSchema?.required?.includes("npc_id"), "get_npc_status 要求 npc_id");

    console.log("\n[4/6] 调用 get_npc_status...");
    const statusRes = await client.callTool("get_npc_status", { npc_id: "npc_001" });
    const statusText = statusRes?.content?.[0]?.text || "";
    console.log("     结果:", statusText);
    assert(statusText.includes("npc_001"), "结果包含 npc_id");
    assert(statusText.includes("[Unity 反馈]"), "结果来自 Unity");
    assert(statusText.includes("状态"), "结果包含'状态'关键字");

    console.log("\n[5/6] 调用 get_npc_position...");
    const posRes = await client.callTool("get_npc_position", { npc_id: "npc_002" });
    const posText = posRes?.content?.[0]?.text || "";
    console.log("     结果:", posText);
    assert(posText.includes("npc_002"), "结果包含 npc_id");
    assert(posText.includes("[Unity 反馈]"), "结果来自 Unity");
    assert(/\d/.test(posText), "结果包含数字坐标");

    console.log("\n[6/6] 调用 move_to 和 say...");
    const moveRes = await client.callTool("move_to", { npc_id: "npc_003", target: "城门" });
    const moveText = moveRes?.content?.[0]?.text || "";
    console.log("     move_to:", moveText);
    assert(moveText.includes("[Unity 反馈]"), "move_to 结果来自 Unity");
    assert(moveText.includes("npc_003") && moveText.includes("城门"), "move_to 结果包含 npc_id 和 target");

    const sayRes = await client.callTool("say", { npc_id: "npc_004", content: "你好，世界！" });
    const sayText = sayRes?.content?.[0]?.text || "";
    console.log("     say    :", sayText);
    assert(sayText.includes("[Unity 反馈]"), "say 结果来自 Unity");
    assert(sayText.includes("npc_004") && sayText.includes("你好，世界"), "say 结果包含 npc_id 和 content");

    console.log("\n[额外] 测试缺少必填参数...");
    try {
      await client.callTool("get_npc_status", {});
      assert(false, "缺少 npc_id 时应报错");
    } catch (err) {
      assert(true, `缺少 npc_id 时正确报错: ${err.message}`);
    }
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
    if (mockUnityProc) {
      console.log("[关闭] 终止 mockUnity 进程...");
      mockUnityProc.kill();
      await sleep(500);
      if (!mockUnityProc.killed) mockUnityProc.kill("SIGKILL");
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
