# @harbor/ide-client

Attach to a running Harbor from any editor, script, or VS Code extension:
spawn `harbor ide --session <id>`, speak newline-delimited JSON-RPC 2.0 over
stdio, inject prompts, read the live stream — while the user's TUI shows the
same run in real time.

Zero dependencies. Node >= 18.

## Library

```js
import { HarborIdeClient, StreamKinds } from '@harbor/ide-client';

const client = await HarborIdeClient.spawn({ sessionId: 'abc123' });

client.on('stream', (n) => {
  if (n.kind === StreamKinds.MessageDelta) process.stdout.write(n.delta);
  if (n.kind === StreamKinds.ToolStart) console.log(`tool: ${n.tool_name}`);
});

await client.readStream();
const { accepted } = await client.injectPrompt('Print hello world in 3 languages');
await client.waitIdle();   // resolves on agent_end / agent_error
await client.dispose();    // closes stdin — bridge exits after drain
```

### API

| Method | JSON-RPC method | Notes |
|---|---|---|
| `listSessions()` | `list_sessions` | Sessions known to the running host |
| `injectPrompt(prompt, agent?)` | `inject_prompt` | Returns `{accepted}` immediately — the run never blocks the bridge |
| `readStream()` / `stopStream()` | `read_stream` / `stop_stream` | Toggle `stream` notifications |
| `abort()` | `abort` | Abort the in-flight run |
| `runPrompt(prompt)` | — | Convenience: readStream + injectPrompt + waitIdle |

Events (EventEmitter): `stream` (session events, kinds in `StreamKinds`),
`prompt_error`, `exit`, `error`, `stderr` (bridge diagnostics).

## CLI

```bash
export KILO_API_KEY=…            # provider auth, as usual
export HARBOR_MODEL=kilocode/tencent/hy3:free

# one-shot: attach, stream, inject, print until agent_end
node harbor-ide.mjs attach --session <id> --prompt "Read the first 10 lines of README.md"

# passive: just stream the session
node harbor-ide.mjs attach --session <id>

# abort the in-flight run
node harbor-ide.mjs abort --session <id>
```

`--bin <path>` (or `HARBOR_BIN`) selects the Harbor binary; the default is
`harbor` on PATH.

## Protocol

Framing: one JSON object per line on the bridge's stdin/stdout.

```jsonc
// request
{"jsonrpc":"2.0","id":1,"method":"inject_prompt","params":{"prompt":"…"}}
// response
{"jsonrpc":"2.0","id":1,"result":{"accepted":true,"session_id":"abc123"}}
// server→editor notification
{"jsonrpc":"2.0","method":"stream","params":{"session_id":"abc123","kind":"message_delta","delta":"Hel"}}
```

Errors use JSON-RPC codes: `-32601` unknown method, `-32602` invalid params,
`-32600` malformed request, `-32000` handler failure, `-32002` request timed
out. Full contract (records, stream kinds, AOT-safe serializer):
`src/Harbor.Ipc.Client/Ide/IdeProtocol.cs`.

## Requirements on the host side

- A running Harbor daemon/TUI in `HARBOR_MODE=ipc-server` (default for the TUI).
- `harbor ide --session <id>` binds the bridge to one session; the spawned
  process owns its stdio — keep console logging off (`HARBOR_LOGLEVEL=None`)
  when wrapping it manually.
