#!/usr/bin/env node
/** A2A + unified Runtime Gateway + MCP smoke test. */
const http = require("http");
const path = require("path");
const { spawn, spawnSync } = require("child_process");

const AGENT_PORT = 18080;
const LLM_PORT = 18092;
const BASE_URL = `http://127.0.0.1:${AGENT_PORT}`;
const TOKEN = "protocol-test-token";
const RUNTIME_ID = "test-runtime-1";
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function readJson(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    req.on("data", (chunk) => { body += chunk; });
    req.on("end", () => {
      try { resolve(JSON.parse(body || "{}")); } catch (error) { reject(error); }
    });
    req.on("error", reject);
  });
}

function startLlmMock() {
  const server = http.createServer(async (req, res) => {
    const request = await readJson(req);
    const hasToolResult = request.messages.some((message) => message.role === "tool");
    res.writeHead(200, { "content-type": "text/event-stream" });
    const delta = hasToolResult
      ? { content: "已到达大门。" }
      : { tool_calls: [{ index: 0, id: "call-1", type: "function",
          function: { name: "game_npc_move",
            arguments: JSON.stringify({ targetId: "landmark:gate" }) } }] };
    res.write(`data: ${JSON.stringify({ choices: [{ delta }] })}\n\n`);
    res.end("data: [DONE]\n\n");
  });
  return new Promise((resolve) =>
    server.listen(LLM_PORT, "127.0.0.1", () => resolve(server)));
}

function startAgentService() {
  return spawn("go", ["run", "./cmd/server"], {
    cwd: __dirname,
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
    env: {
      ...process.env,
      AGENT_SERVICE_ADDR: `127.0.0.1:${AGENT_PORT}`,
      AGENT_SERVICE_BASE_URL: BASE_URL,
      A2A_BEARER_TOKEN: TOKEN,
      RUNTIME_GATEWAY_TOKEN: TOKEN,
      MCP_GATEWAY_SERVICE_TOKEN: TOKEN,
      LLM_API_URL: `http://127.0.0.1:${LLM_PORT}/v1/chat/completions`,
      LLM_API_KEY: "test-key",
      LLM_MODEL: "mock-model",
      GOCACHE: process.env.GOCACHE ||
        path.join(__dirname, "..", ".cache", "go-build"),
    },
  });
}

function stopAgentService(agent) {
  if (process.platform === "win32") {
    spawnSync("taskkill", ["/pid", String(agent.pid), "/T", "/F"], {
      stdio: "ignore", windowsHide: true,
    });
  } else {
    agent.kill("SIGTERM");
  }
}

async function waitForHealth() {
  for (let i = 0; i < 100; i++) {
    try {
      const response = await fetch(`${BASE_URL}/health`);
      if (response.ok) return;
    } catch {}
    await sleep(100);
  }
  throw new Error("Agent Service did not become healthy");
}

function startRuntimeMock() {
  return new Promise((resolve, reject) => {
    const socket = new WebSocket(`ws://127.0.0.1:${AGENT_PORT}/runtime/ws`);
    const timeout = setTimeout(
      () => reject(new Error("Mock Runtime initialization timed out")), 5000);
    socket.addEventListener("open", () => socket.send(JSON.stringify({
      jsonrpc: "2.0",
      id: "runtime-initialize-1",
      method: "runtime.initialize",
      params: {
        token: TOKEN,
        manifest: {
          instanceId: RUNTIME_ID,
          revision: 1,
          entities: ["Ryan_001"],
          tools: [{
            name: "game_npc_move",
            description: "Move a game entity",
            inputSchema: {
              type: "object",
              properties: {
                entityId: { type: "string" },
                targetId: { type: "string" },
              },
              required: ["entityId", "targetId"],
              additionalProperties: false,
            },
          }],
        },
      },
    })));
    socket.addEventListener("message", (event) => {
      const message = JSON.parse(String(event.data));
      if (message.id === "runtime-initialize-1") {
        clearTimeout(timeout);
        if (message.error || message.result?.accepted !== true)
          reject(new Error("Runtime Gateway rejected Mock Runtime"));
        else
          resolve(socket);
        return;
      }
      if (message.method !== "runtime.tools.call") return;
      const args = message.params?.arguments;
      if (args?.entityId !== "Ryan_001") {
        socket.send(JSON.stringify({
          jsonrpc: "2.0", id: message.id,
          error: { code: -32602, message: "entityId was not bound" },
        }));
        return;
      }
      socket.send(JSON.stringify({
        jsonrpc: "2.0",
        id: message.id,
        result: {
          content: [{ type: "text", text: "arrived" }],
          structuredContent: { ok: true, data: { targetId: args.targetId } },
          isError: false,
        },
      }));
    });
    socket.addEventListener("error", () => {
      clearTimeout(timeout);
      reject(new Error("Mock Runtime WebSocket failed"));
    });
  });
}

async function verifyVirtualMcp() {
  const response = await fetch(`${BASE_URL}/mcp/runtimes/${RUNTIME_ID}`, {
    method: "POST",
    headers: {
      authorization: `Bearer ${TOKEN}`,
      "content-type": "application/json",
      "mcp-protocol-version": "2025-11-25",
    },
    body: JSON.stringify({
      jsonrpc: "2.0", id: "mcp-list-1", method: "tools/list", params: {},
    }),
  });
  const body = await response.text();
  if (!response.ok || !body.includes("game_npc_move"))
    throw new Error(`Virtual MCP tools/list failed: ${body}`);
}

async function run() {
  if (!process.argv.includes("--start-server"))
    throw new Error("This smoke test requires --start-server");
  const llm = await startLlmMock();
  const agent = startAgentService();
  let runtime;
  let stderr = "";
  agent.stdout.resume();
  agent.stderr.on("data", (data) => { stderr += data.toString(); });
  try {
    await waitForHealth();
    runtime = await startRuntimeMock();
    await verifyVirtualMcp();
    const card = await fetch(`${BASE_URL}/.well-known/agent-card.json`);
    if (!card.ok || !(await card.text()).includes("game-npc-conversation"))
      throw new Error("A2A Agent Card validation failed");
    if ((await fetch(`${BASE_URL}/unity/ws`)).status !== 404)
      throw new Error("legacy /unity/ws route is still available");

    const response = await fetch(`${BASE_URL}/a2a`, {
      method: "POST",
      headers: {
        authorization: `Bearer ${TOKEN}`,
        "content-type": "application/json",
        accept: "text/event-stream",
      },
      body: JSON.stringify({
        jsonrpc: "2.0",
        id: "smoke-1",
        method: "message/stream",
        params: { message: {
          messageId: "player-message-1",
          role: "user",
          parts: [{ kind: "text", text: "去大门" }],
          metadata: {
            "https://gamewithllm.dev/extensions/game-context/v1": {
              instanceId: RUNTIME_ID,
              playerId: "player-1",
              agentId: "Ryan_001",
              sceneId: "warehouse-demo",
            },
          },
        }},
      }),
      signal: AbortSignal.timeout(30000),
    });
    const stream = await response.text();
    if (!response.ok || !stream.includes("artifact-update") ||
        !stream.includes("completed") || !stream.includes("已到达大门"))
      throw new Error(`A2A + Runtime Gateway tool loop failed: ${stream}`);
    console.log(
      "[通过] A2A streaming、统一 Runtime Gateway、虚拟 MCP 和 v2 删除检查");
  } finally {
    runtime?.close(1000, "test complete");
    stopAgentService(agent);
    llm.closeAllConnections();
    await new Promise((resolve) => llm.close(resolve));
  }
  if (stderr.includes("panic")) throw new Error(stderr);
}

run().catch((error) => {
  console.error("[失败]", error);
  process.exitCode = 1;
});
