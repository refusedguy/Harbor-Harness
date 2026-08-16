# ADR-005: MCP Integration Architecture

**Status:** Accepted  
**Date:** 2026-08-16  
**Authors:** Harbor team  
**Context:** Large refactoring & production stabilization

---

## 1. Контекст

Текущий MCP — stub-only:
- `IMcpRegistry` — in-memory, `InvokeAsync` always fails
- `McpToolTool` — `ITool` implementation, delegates to `IMcpRegistry`
- `harbor.mcp.json` — не существует

Нужен production-ready MCP integration:
1. External MCP servers via stdio JSON-RPC 2.0
2. Map MCP tools to `ITool` seamlessly
3. AOT-safe JSON serialization

---

## 2. Решение

### 2.1 Architecture

```
McpProcessClient (spawns child process)
    ↓ stdio (RedirectStandardInput/Output)
McpJsonRpcTransport (JSON-RPC 2.0 framing)
    ↓
McpRegistry (in-memory, AOT-safe)
    ↓
McpToolAdapter : ITool
    ↓
ToolRegistry.Register(...)
```

### 2.2 Components

| Component | Responsibility |
|---|---|
| `McpProcessClient` | Spawns child process, manages stdio streams |
| `McpJsonRpcTransport` | JSON-RPC 2.0 framing over Stream |
| `McpRegistry` | In-memory registry of MCP tools |
| `McpToolAdapter` | Maps MCP tool → `ITool` |
| `harbor.mcp.json` | Configuration for external MCP servers |

### 2.3 JSON Serialization

- `System.Text.Json` + `JsonSerializerContext` (source generation)
- No reflection, no `JsonDocument.Parse` on hot path
- AOT-safe

---

## 3. Последствия

| Что меняется | Что нет |
|---|---|
| `src/Harbor.Tools.Mcp/` — полная переработка | `McpToolTool` — остаётся как façade |
| `IMcpRegistry` — новый контракт | `ITool` — не меняется |
| `harbor.mcp.json` — новый конфиг | `ToolRegistry` — не меняется |

---

## 4. Правила

1. **MCP tools = first-class ITools** — регистрируются в `ToolRegistry` как любые другие
2. **Process spawning = opt-in** — только для внешних MCP серверов
3. **JSON-RPC 2.0 strict** — no custom extensions
4. **AOT-safe serialization** — `JsonSerializerContext` everywhere
