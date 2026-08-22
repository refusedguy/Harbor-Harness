#!/usr/bin/env python3
"""Minimal Model Context Protocol (MCP) stdio server — Python, stdlib only.

Speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on stdin/stdout.
Logs go to stderr. Exposes a single `echo` tool.

Run:  python3 main.py
"""
import json
import sys


PROTOCOL_VERSION = "2024-11-05"
SERVER_NAME = "python-hello"
SERVER_VERSION = "0.1.0"


def send(obj):
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def handle_initialize(msg_id):
    send({
        "jsonrpc": "2.0",
        "id": msg_id,
        "result": {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        },
    })


def handle_tools_list(msg_id):
    send({
        "jsonrpc": "2.0",
        "id": msg_id,
        "result": {
            "tools": [
                {
                    "name": "echo",
                    "description": "Echo the provided text back to the caller.",
                    "inputSchema": {
                        "type": "object",
                        "properties": {"text": {"type": "string"}},
                        "required": ["text"],
                    },
                }
            ]
        },
    })


def handle_tools_call(msg_id, params):
    name = params.get("name", "")
    arguments = params.get("arguments", {}) or {}
    text = arguments.get("text", "")
    send({
        "jsonrpc": "2.0",
        "id": msg_id,
        "result": {
            "content": [{"type": "text", "text": text}],
            "isError": False,
        },
    })


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as ex:
            sys.stderr.write(f"[python-hello] bad json: {ex}\n")
            continue

        method = msg.get("method")
        msg_id = msg.get("id")
        params = msg.get("params", {}) or {}

        if method == "initialize":
            handle_initialize(msg_id)
        elif method == "notifications/initialized":
            continue
        elif method == "ping":
            if msg_id is not None:
                send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
        elif method == "tools/list":
            handle_tools_list(msg_id)
        elif method == "tools/call":
            handle_tools_call(msg_id, params)
        else:
            if msg_id is not None:
                send({
                    "jsonrpc": "2.0",
                    "id": msg_id,
                    "error": {"code": -32601, "message": f"Method not found: {method}"},
                })


if __name__ == "__main__":
    main()
