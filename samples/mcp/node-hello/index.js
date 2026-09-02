#!/usr/bin/env node
/* Minimal Model Context Protocol (MCP) stdio server — Node.js, stdlib only.
 *
 * Speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on stdin/stdout.
 * Logs go to stderr. Exposes a single `echo` tool.
 *
 * Run:  node index.js
 */
"use strict";

const PROTOCOL_VERSION = "2024-11-05";
const SERVER_NAME = "node-hello";
const SERVER_VERSION = "0.1.0";

function send(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

function handleInitialize(id) {
  send({
    jsonrpc: "2.0",
    id,
    result: {
      protocolVersion: PROTOCOL_VERSION,
      capabilities: { tools: { listChanged: false } },
      serverInfo: { name: SERVER_NAME, version: SERVER_VERSION },
    },
  });
}

function handleToolsList(id) {
  send({
    jsonrpc: "2.0",
    id,
    result: {
      tools: [
        {
          name: "echo",
          description: "Echo the provided text back to the caller.",
          inputSchema: {
            type: "object",
            properties: { text: { type: "string" } },
            required: ["text"],
          },
        },
      ],
    },
  });
}

function handleToolsCall(id, params) {
  const args = (params && params.arguments) || {};
  const text = typeof args.text === "string" ? args.text : "";
  send({
    jsonrpc: "2.0",
    id,
    result: {
      content: [{ type: "text", text }],
      isError: false,
    },
  });
}

let buffer = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => {
  buffer += chunk;
  let nl;
  while ((nl = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, nl).trim();
    buffer = buffer.slice(nl + 1);
    if (!line) continue;

    let msg;
    try {
      msg = JSON.parse(line);
    } catch (ex) {
      process.stderr.write("[node-hello] bad json: " + ex.message + "\n");
      continue;
    }

    const method = msg.method;
    const id = msg.id;
    const params = msg.params || {};

    if (method === "initialize") {
      handleInitialize(id);
    } else if (method === "notifications/initialized") {
      // no response
    } else if (method === "ping") {
      if (id !== undefined && id !== null) send({ jsonrpc: "2.0", id, result: {} });
    } else if (method === "tools/list") {
      handleToolsList(id);
    } else if (method === "tools/call") {
      handleToolsCall(id, params);
    } else if (id !== undefined && id !== null) {
      send({ jsonrpc: "2.0", id, error: { code: -32601, message: "Method not found: " + method } });
    }
  }
});
