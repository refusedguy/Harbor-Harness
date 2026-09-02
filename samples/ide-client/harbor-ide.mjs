#!/usr/bin/env node
/**
 * Harbor IDE client — attach to a running Harbor from any editor or script.
 *
 * Zero dependencies (Node >= 18). Speaks newline-delimited JSON-RPC 2.0 with
 * the `harbor ide --session <id>` stdio bridge:
 *
 *   import { HarborIdeClient } from './harbor-ide.mjs';
 *   const client = await HarborIdeClient.spawn({ sessionId: 'abc123' });
 *   client.on('stream', n => process.stdout.write(n.delta ?? ''));
 *   await client.readStream();
 *   await client.injectPrompt('Print hello world in 3 languages');
 *   await client.waitIdle();
 *   await client.dispose();
 *
 * One-shot CLI (mirrors the acceptance flow):
 *   node harbor-ide.mjs attach --session <id> --prompt "…"   # attach + stream + prompt
 *   node harbor-ide.mjs abort  --session <id>                # abort the in-flight run
 *
 * Protocol reference: src/Harbor.Ipc.Client/Ide/IdeProtocol.cs
 */

import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { EventEmitter } from 'node:events';

/** JSON-RPC 2.0 request id counter source — one client = one bridge process. */
let nextRequestId = 1;

/** Default per-request budget for bridge calls (must stay < bridge's 2 min cap). */
const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;

/**
 * Typed event names pushed by the bridge as `stream` notifications.
 * Mirrors IdeStreamKinds in the C# protocol contract.
 */
export const StreamKinds = Object.freeze({
  AgentStart: 'agent_start',
  MessageDelta: 'message_delta',
  MessageEnd: 'message_end',
  ToolStart: 'tool_start',
  ToolEnd: 'tool_end',
  TurnStart: 'turn_start',
  TurnEnd: 'turn_end',
  AgentEnd: 'agent_end',
  AgentError: 'agent_error',
});

/**
 * A client bound to one `harbor ide` bridge process.
 *
 * The bridge NEVER blocks the agent loop, so injectPrompt resolves as soon as
 * the run is accepted; the actual output arrives as `stream` notifications.
 * @extends {EventEmitter}
 */
export class HarborIdeClient extends EventEmitter {
  /**
   * @param {import('node:child_process').ChildProcessWithoutNullStreams} child
   * @param {{ command: string, args: string[] }} spawnSpec
   */
  constructor(child, spawnSpec) {
    super();
    this.#child = child;
    this.#spawnSpec = spawnSpec;
    this.#pending = new Map();

    const rl = createInterface({ input: child.stdout });
    rl.on('line', (line) => this.#onLine(line));
    child.stderr.on('data', (chunk) => this.emit('stderr', String(chunk)));
    child.on('exit', (code, signal) => {
      this.#exited = true;
      for (const { reject } of this.#pending.values()) {
        reject(new Error(`harbor ide exited (code=${code} signal=${signal}) before answering`));
      }
      this.#pending.clear();
      this.emit('exit', code, signal);
    });
    child.on('error', (err) => this.emit('error', err));
  }

  #child;
  #spawnSpec;
  /** @type {Map<number, { resolve: (v: any) => void, reject: (e: Error) => void, timer: NodeJS.Timeout }>} */
  #pending;
  #exited = false;

  /** True once the bridge process exited (stdin EOF or editor-side kill). */
  get exited() {
    return this.#exited;
  }

  /**
   * Spawn `harbor ide --session <id>` and speak the NDJSON protocol.
   * @param {{
   *   sessionId: string,
   *   command?: string,          // default: HARBOR_BIN ?? 'harbor'
   *   requestTimeoutMs?: number, // default: 30s
   *   cwd?: string,
   *   env?: NodeJS.ProcessEnv
   * }} options
   */
  static async spawn(options) {
    const command = options.command ?? process.env.HARBOR_BIN ?? 'harbor';
    const args = ['ide', '--session', options.sessionId];
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: { ...process.env, ...options.env },
      stdio: ['pipe', 'pipe', 'pipe'],
    });
    const client = new HarborIdeClient(child, { command, args });
    // Wait for the process to come up — an immediate non-zero exit means the
    // session was not found / no daemon is running.
    await new Promise((resolve, reject) => {
      const onError = (err) => { cleanup(); reject(err); };
      const onExit = (code, signal) => { cleanup(); reject(new Error(`${command} exited (code=${code} signal=${signal}) during startup`)); };
      const cleanup = () => {
        child.off('error', onError);
        child.off('exit', onExit);
      };
      child.on('error', onError);
      child.on('exit', onExit);
      // The bridge answers nothing at attach time; a short healthy delay is
      // the startup signal. Requests are queued against a live process.
      setTimeout(() => { cleanup(); resolve(); }, 150);
    });
    return client;
  }

  /**
   * Send a request and await its response.
   * @template T
   * @param {string} method
   * @param {object} [params]
   * @param {number} [timeoutMs]
   * @returns {Promise<T>}
   */
  async request(method, params, timeoutMs = DEFAULT_REQUEST_TIMEOUT_MS) {
    if (this.#exited) throw new Error('bridge already exited');
    const id = nextRequestId++;
    const frame = { jsonrpc: '2.0', id, method };
    if (params !== undefined) frame.params = params;

    const result = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new Error(`bridge request '${method}' timed out after ${timeoutMs}ms`));
      }, timeoutMs);
      this.#pending.set(id, { resolve, reject, timer });
    });

    this.#child.stdin.write(JSON.stringify(frame) + '\n');
    return result;
  }

  /** List sessions known to the running Harbor host. */
  listSessions() {
    return this.request('list_sessions');
  }

  /**
   * Submit a prompt. Resolves immediately with `{ accepted, session_id }`;
   * output streams via the `stream` event.
   */
  injectPrompt(prompt, agent = undefined) {
    return this.request('inject_prompt', { prompt, ...(agent ? { agent } : {}) });
  }

  /** Start forwarding session events as `stream` notifications. */
  readStream() {
    return this.request('read_stream');
  }

  /** Stop forwarding session events. */
  stopStream() {
    return this.request('stop_stream');
  }

  /** Abort the in-flight run (and cancel pending injects). */
  abort() {
    return this.request('abort');
  }

  /**
   * Convenience: inject a prompt and resolve when the run completes
   * (agent_end / prompt_error / agent_error notification arrives).
   */
  async runPrompt(prompt) {
    await this.readStream();
    const done = new Promise((resolve, reject) => {
      const onStream = (n) => {
        if (n.kind === StreamKinds.AgentEnd || n.kind === StreamKinds.AgentError) {
          this.off('stream', onStream);
          this.off('prompt_error', onError);
          resolve();
        }
      };
      const onError = (n) => {
        this.off('stream', onStream);
        this.off('prompt_error', onError);
        reject(new Error(n.error));
      };
      this.on('stream', onStream);
      this.on('prompt_error', onError);
    });
    await this.injectPrompt(prompt);
    return done;
  }

  /** Alias for {@link runPrompt} semantics — resolves when idle again. */
  async waitIdle() {
    return new Promise((resolve) => {
      const onStream = (n) => {
        if (n.kind === StreamKinds.AgentEnd || n.kind === StreamKinds.AgentError) {
          this.off('stream', onStream);
          resolve();
        }
      };
      this.on('stream', onStream);
    });
  }

  /** Close stdin — the bridge exits after draining in-flight work. */
  async dispose() {
    if (!this.#exited) {
      this.#child.stdin.end();
      await new Promise((resolve) => {
        if (this.#exited) return resolve();
        this.#child.once('exit', resolve);
        setTimeout(resolve, 10_000).unref();
      });
    }
  }

  /** @param {string} line */
  #onLine(line) {
    if (!line.trim()) return;
    let frame;
    try {
      frame = JSON.parse(line);
    } catch {
      this.emit('error', new Error(`non-JSON line from bridge: ${line.slice(0, 200)}`));
      return;
    }

    if (frame.method !== undefined) {
      // Server→editor notification.
      this.emit(frame.method, frame.params);
      return;
    }

    const pending = this.#pending.get(frame.id);
    if (!pending) return;
    this.#pending.delete(frame.id);
    clearTimeout(pending.timer);
    if (frame.error !== undefined) {
      const err = new Error(frame.error.message ?? 'bridge error');
      err.code = frame.error.code;
      pending.reject(err);
    } else {
      pending.resolve(frame.result);
    }
  }
}

// ── CLI ────────────────────────────────────────────────────────────────────

function usage() {
  console.error(`usage: node harbor-ide.mjs <command> [options]

commands:
  attach --session <id> [--prompt <text>]  attach, stream, optionally inject a prompt
  abort  --session <id>                    abort the in-flight run

options:
  --bin <path>   harbor binary (default: HARBOR_BIN ?? 'harbor')
  --agent <name> agent for inject_prompt (optional)`);
  process.exit(2);
}

function parseArgs(argv) {
  const opts = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const value = argv[i + 1] && !argv[i + 1].startsWith('--') ? argv[++i] : true;
      opts[key] = value;
    } else {
      opts._.push(a);
    }
  }
  return opts;
}

export async function main(argv) {
  const [command, ...rest] = argv;
  if (!command) usage();
  const opts = parseArgs(rest);
  if (!opts.session) usage();

  const client = await HarborIdeClient.spawn({ sessionId: opts.session, command: opts.bin });
  client.on('stream', (n) => {
    switch (n.kind) {
      case StreamKinds.MessageDelta: process.stdout.write(n.delta ?? ''); break;
      case StreamKinds.MessageEnd: process.stdout.write('\n'); break;
      case StreamKinds.ToolStart: console.error(`\n[tool] ${n.tool_name} …`); break;
      case StreamKinds.ToolEnd: console.error(`[tool] ${n.tool_name} → ${n.ok ? 'ok' : 'error'}`); break;
      case StreamKinds.AgentError: console.error(`\n[agent_error] ${n.error}`); break;
      case StreamKinds.AgentStart: console.error('[agent_start]'); break;
      case StreamKinds.AgentEnd: console.error('\n[agent_end]'); break;
      default: break;
    }
  });
  client.on('prompt_error', (n) => console.error(`[prompt_error] ${n.error}`));
  client.on('stderr', (line) => console.error(`[bridge] ${line.trimEnd()}`));

  if (command === 'abort') {
    const res = await client.abort();
    console.error(`abort requested=${res.requested}`);
    await client.dispose();
    return 0;
  }

  if (command === 'attach') {
    if (typeof opts.prompt === 'string') {
      await client.runPrompt(opts.prompt);
    } else {
      await client.readStream();
      console.error('attached — streaming (Ctrl+C to detach)');
      await new Promise(() => {});
    }
    await client.dispose();
    return 0;
  }

  usage();
  return 2;
}

if (process.argv[1] && import.meta.url.endsWith(process.argv[1].split('/').pop())) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (err) => {
      console.error(`harbor-ide: ${err.message}`);
      process.exit(1);
    },
  );
}
