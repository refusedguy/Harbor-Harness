# Security Audit — Threat Model

Remainder of item 3 of the v1.0 security audit after the disclosure-policy
slice landed. The disclosure policy, supported versions, and response
expectations live in the [Security Policy](../SECURITY.md) — this document
does not repeat them. Background comparison with peer tools lives in
[Security and Sandboxing Analysis](./security-sandboxing-analysis.md).

Scope: threat model only — six trust surfaces below, each with an explicit
verdict. Out of scope: dependency audit and dependency-review CI job
(tracked separately, not built here); per-finding fix PRs (each open finding
below names the follow-up instead of fixing it here).

Related operator docs: [Plugin Development](./PLUGIN_DEVELOPMENT.md),
[Tools Catalog](./TOOLS_CATALOG.md), [Configuration](./CONFIGURATION.md).

## Trust boundaries in one picture

Untrusted input enters through the CLI prompt, plugin source files, MCP
child processes, and provider network responses. The agent loop turns that
input into tool calls. Tool calls touch the filesystem, the shell, the
network, and the local IPC channel. Secrets sit beside all of it in config
files and environment variables. There is no OS-level sandbox: the only
enforcement is application-level permission rules plus user confirmation.

## 1. CLI input

Includes the interactive prompt, slash commands, pasted content, and anything
the model quotes back into a tool argument.

Attacker: anyone who can get text into the prompt — a pasted web page, a file
the agent reads, a provider response — attempting prompt injection that
steers the next tool call.

Mitigations today: tool arguments are validated per tool; destructive shell
patterns and parent-directory escapes are matched before execution; the
default ruleset asks before most writes outside the source tree and denies
writes to credential-shaped paths and destructive shell invocations; the
plan-flavoured agents deny shell and edit entirely.

Verdict: mitigated, with accepted residual risk. Application-level matching
is not isolation: a novel phrasing the matcher does not recognise still
reaches the confirmation prompt, and confirmation fatigue is real.

User guidance: treat pasted third-party text as untrusted; prefer the
read-only agent flavours for triage; keep the default ask behaviour on for
shell and network tools.

## 2. Plugins (CS-source and DLL)

Drop-in source files compile at startup and run in-process with full trust;
DLL plugins load the same way. A plugin can do anything the Harbor process
can do: read files, open sockets, spawn processes.

Attacker: a plugin author, or anyone who can plant a file in the plugin
directories (shared machine, dotfiles repo, malicious snippet).

Mitigations today: plugin discovery paths are documented and narrow; the
plugin guide states the full-trust model plainly; the closed UI extension
seam is collected but never rendered, so a plugin cannot silently add an
input-capturing view through that path; there is no claim of sandboxing.

Verdict: accepted risk by design. This is the largest surface in the
product and it is intentional — review-before-install is the control.

User guidance: install only plugin source you have read; keep plugin
directories out of shared or world-writable locations; re-read plugins after
updates before restarting.

## 3. MCP servers

External processes (sample servers span several runtimes) speak a JSON
protocol over stdio or HTTP and their tool outputs flow back into the agent
context as tool results.

Attacker: a malicious or compromised MCP server returning crafted tool
output — exfiltration prompts, credential-shaped strings, oversized payloads
aimed at context exhaustion.

Mitigations today: MCP tool calls go through the same allow/ask/deny path
as built-in tools and default to asking; server output is data, never
executed as code by the host; per-server allowlists are supported.

Verdict: mitigated, with one open finding (see below): no per-server output
size cap or schema-strictness gate is documented, so a chatty server can
still bloat context.

User guidance: allowlist only the MCP tools you need per server; treat
server descriptions and results as untrusted text, not instructions.

## 4. Providers (LLM APIs)

Provider clients send prompts and receive text plus tool-call requests over
HTTPS. Provider configuration is data files plus an API key in the
environment; model lists are fetched from provider endpoints.

Attacker: a malicious model-list endpoint, a man-in-the-middle on a custom
base URL, or a provider response carrying injected tool-call arguments.

Mitigations today: keys are read from the environment, never written into
session logs by design; custom base URLs are explicit user configuration;
tool-call arguments from the model pass through the same validation and
permission path as any other caller; timeouts and retries are bounded per
provider config.

Verdict: mitigated. The residual risk is the standard one for any API client:
a custom endpoint the user configured is fully trusted with prompt content.

User guidance: use HTTPS endpoints only; keep provider keys in the
environment, not in config files or chat; review custom provider files
before adding them.

## 5. IPC and remote transport

The daemon and remote UI channel carry agent events and tool traffic over a
local channel; the remote transport option extends that channel beyond the
machine.

Attacker: another local user or process connecting to the channel, or a
network observer on the remote path.

Mitigations today: the channel is local-first; remote use is opt-in
configuration, not default; event payloads are typed records, not raw
eval strings.

Verdict: mitigated for local use; accepted risk when remote transport is
enabled — the operator accepts the network they point it at.

User guidance: keep remote transport off unless you need it; when enabled,
run it over a private network path you already trust.

## 6. Secrets (config, environment, logs, telemetry)

Credentials live in the Harbor home config file and in provider key
environment variables. Logs, session stores, and optional telemetry exports
sit nearby.

Attacker: log sharing, session-file sharing, or a tool result that echoes a
secret back into stored context.

Mitigations today: the default ruleset denies edits to credential-shaped
paths; the disclosure policy tells reporters to redact keys before sharing
logs; telemetry export is opt-in.

Verdict: mitigated, with accepted residual risk. No scanner today proves a
secret never lands in a session file — redaction is a convention the
operator must follow.

User guidance: never paste keys into chat, issues, or shared sessions;
redact before running the logs command output anywhere public; keep the
Harbor home directory at default permissions.

## Shell execution note

The shell tool deserves a callout because it amplifies every surface above:
a successful injection anywhere else cashes out through shell execution.
The default ruleset allows a small list of read-only commands, denies the
known-destructive shapes, and asks for the rest; the read-only agent
flavours deny shell entirely. That matches the accepted-risk posture:
the shell is not sandboxed at OS level, so the confirmation prompt is the
boundary. Do not turn the shell to blanket allow.

## Telemetry note

Telemetry export sends operational data to a configured endpoint. It is off
unless configured, carries no credentials by design, and inherits the
provider-endpoint trust rule: the endpoint you configure sees what you send
it. No separate verdict — covered by the provider and secrets sections.

## Verdict summary

| Surface | Verdict |
| ------- | ------- |
| CLI input | Mitigated, residual prompt-injection risk accepted |
| Plugins | Accepted risk by design, review-before-install is the control |
| MCP servers | Mitigated, output-size hardening left as open finding |
| Providers | Mitigated, custom endpoint trusted with prompt content |
| IPC and remote transport | Mitigated locally, network risk accepted when remote is on |
| Secrets | Mitigated, redaction remains operator duty |

Open findings carried out of this document:

- MCP output-size and schema-strictness hardening has no tracking issue yet;
  file one before claiming this surface is done.
- No secret-presence scan over session stores exists; until one lands, the
  redaction rule stays a convention, not a gate.

## How to re-check this document

No compiled code is involved. The checks are the docs gates:

```sh
python3 tools/md-lint.py --min-files 250 --min-lines 60000
python3 tools/check-md-links.py --min-files 250 --min-refs 600
```

Both must exit zero. This document must stay link-clean and shape-clean
under those two commands, and must not add a new checker without removing
an old one.
