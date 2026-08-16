# ADR-006: Daemon Mode, Remote Gateway & Terminal QR Pairing

**Status:** Accepted  
**Date:** 2026-08-16  
**Authors:** Harbor team  
**Context:** Large refactoring & production stabilization

---

## 1. Контекст

Текущая архитектура:
- IPC via named pipes (Windows) / Unix domain sockets (Linux/macOS)
- Two-process ready: `ipc-server` + `ipc-client` modes
- `Harbor.App.Cli` supports `HARBOR_MODE=inprocess|ipc-server|ipc-client`

Нужно добавить:
1. **Daemon lifecycle** — `harbor daemon start|stop|status`
2. **Remote WebSocket transport** — Kestrel WebSocket listener с PSK auth
3. **Terminal QR pairing** — Unicode QR в терминале для connection URL

---

## 2. Решение

### 2.1 Daemon Lifecycle

```
harbor daemon start
├── Spawn detached process: harbor --headless --remote
├── Store PID in ~/.harbor/daemon.pid
├── Write ephemeral PSK to ~/.harbor/daemon.psk
└── Output terminal QR with connection URL

harbor daemon stop
├── Read PID from ~/.harbor/daemon.pid
├── Send SIGTERM
└── Clean up PID/PSK files

harbor daemon status
└── Check if PID is alive + WebSocket listener responding
```

### 2.2 Remote WebSocket Transport

**New project:** `Harbor.Transport.Remote`

| Component | Responsibility |
|---|---|
| `RemoteGateway` | Embedded Kestrel + WebSocket listener |
| `RemoteClient` | WebSocket client connecting to gateway |
| `UiTransportPacket` | DTO for event serialization |
| `PsAuthHandler` | Ephemeral PSK validation |

**Security:**
- PSK generated on startup: 256-bit random, base64-encoded
- PSK expires after 24h or daemon restart
- No TLS for local loopback (Unix socket / localhost only)

### 2.3 Terminal QR Renderer

```
TerminalQrRenderer
├── Unicode half-blocks: █, ▀, ▄
├── No GDI/drawing dependencies
├── Pure console output
└── Supports QR versions 1–10 (URL + token fits in V3)
```

**Implementation:** qrcode-generator in pure C#, render to Unicode blocks.

---

## 3. Последствия

| Что меняется | Что нет |
|---|---|
| `Harbor.App.Cli` — new subcommands | Existing IPC code — не трогаем |
| `Harbor.Transport.Remote` — новый проект | `Harbor.Ipc.*` — остаётся |
| `TerminalQrRenderer` — новый файл | Spectre TUI — не трогаем |

---

## 4. Правила

1. **Daemon = separate process** — не in-process
2. **WebSocket only on localhost** — no remote exposure by default
3. **QR = best-effort** — fallback to plain URL if terminal too narrow
4. **PSK ephemeral** — no persistent credentials
