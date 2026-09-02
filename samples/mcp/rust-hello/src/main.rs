// Minimal Model Context Protocol (MCP) stdio server — Rust.
//
// Speaks standard MCP JSON-RPC 2.0 over newline-delimited JSON on stdin/stdout and logs
// to stderr. Exposes a single `echo` tool. Build/run with `cargo run` (or `cargo build
// --release` and run the binary directly from mcp.json).

use std::io::{self, BufRead, Write};
use std::sync::atomic::{AtomicBool, Ordering};

const PROTOCOL_VERSION: &str = "2024-11-05";

fn write_json(line: &str) {
    let stdout = io::stdout();
    let mut h = stdout.lock();
    let _ = h.write_all(line.as_bytes());
    let _ = h.write_all(b"\n");
    let _ = h.flush();
}

fn main() {
    let running = AtomicBool::new(true);
    let stdin = io::stdin();
    for raw in stdin.lock().lines() {
        let line = match raw {
            Ok(l) => l,
            Err(_) => break,
        };
        let trimmed = line.trim();
        if trimmed.is_empty() {
            continue;
        }
        if let Ok(v) = serde_json::from_str::<serde_json::Value>(trimmed) {
            handle(&v);
        } else {
            eprintln!("[rust-hello] bad json");
        }
        if !running.load(Ordering::Relaxed) {
            break;
        }
    }
}

fn handle(msg: &serde_json::Value) {
    let method = msg.get("method").and_then(|m| m.as_str()).unwrap_or("");
    let id = msg.get("id");
    let has_id = id.is_some();

    match method {
        "initialize" => {
            if has_id {
                write_json(&format!(
                    r#"{{"jsonrpc":"2.0","id":{},"result":{{"protocolVersion":"{}","capabilities":{{"tools":{{"listChanged":false}}}},"serverInfo":{{"name":"rust-hello","version":"0.1.0"}}}}}}}"#,
                    id_str(id),
                    PROTOCOL_VERSION
                ));
            }
        }
        "notifications/initialized" => {}
        "ping" => {
            if has_id {
                write_json(&format!(r#"{{"jsonrpc":"2.0","id":{},"result":{{}}}}"#, id_str(id)));
            }
        }
        "tools/list" => {
            if has_id {
                write_json(
                    r#"{"jsonrpc":"2.0","id":__ID__,"result":{"tools":[{"name":"echo","description":"Echo the provided text back to the caller.","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}]}}"#
                        .replace("__ID__", &id_str(id)),
                );
            }
        }
        "tools/call" => {
            let mut text = String::new();
            if let Some(params) = msg.get("params").and_then(|p| p.get("arguments")) {
                if let Some(t) = params.get("text").and_then(|t| t.as_str()) {
                    text = t.to_string();
                }
            }
            if has_id {
                write_json(&format!(
                    r#"{{"jsonrpc":"2.0","id":{},"result":{{"content":[{{"type":"text","text":{}}}],"isError":false}}}}"#,
                    id_str(id),
                    serde_json::to_string(&text).unwrap_or_else(|_| "\"\"".to_string())
                ));
            }
        }
        _ => {
            if has_id {
                write_json(&format!(
                    r#"{{"jsonrpc":"2.0","id":{},"error":{{"code":-32601,"message":"Method not found: {}"}}}}"#,
                    id_str(id), method
                ));
            }
        }
    }
}

fn id_str(id: Option<&serde_json::Value>) -> String {
    match id {
        Some(serde_json::Value::Number(n)) => n.to_string(),
        Some(serde_json::Value::String(s)) => format!("\"{}\"", s),
        _ => "null".to_string(),
    }
}
