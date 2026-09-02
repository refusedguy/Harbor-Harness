# Security & Sandboxing Patterns in Modern AI Coding Tools

> Analysis date: 2026-08-27  
> Scope: Codex CLI, Cursor, Kilo, and implications for Harbor  
> Repository: Harbor-Harness (branch `dev`)

---

## 1. How Untrusted Code/Tools Are Executed

### 1.1 Codex CLI (OpenAI)

Codex executes every tool invocation inside a **platform-specific OS sandbox**. The four-layer command safety model is:

1. **OS-Native Sandbox** — Seatbelt (macOS), Bubblewrap + Landlock + seccomp-BPF (Linux), Restricted Tokens + DACLs (Windows)
2. **Approval Policy** — `untrusted` / `on-request` / `never`
3. **Command Validation** — Git hook blocking, PowerShell AST safety classification, shell metacharacter detection
4. **Environment Filtering** — `KEY`/`SECRET`/`TOKEN` stripping from subprocess environments, proxy env overrides

**Key execution details:**
- On **macOS**: uses `sandbox-exec` with a dynamically generated Seatbelt profile. The profile restricts syscalls, file reads/writes, and network access. Network is binary (allowed or fully denied) — no domain-level filtering.
- On **Linux**: uses `bubblewrap` for user-namespace isolation with `seccomp` BPF filtering. `seccomp` blocks dangerous syscalls (`connect`, `accept`, `bind`, `listen`, `sendto`, `sendmsg`). `Landlock` enforces filesystem restrictions. Requires unprivileged user-namespace support; AppArmor can interfere unless `bwrap-userns-restrict` profile is loaded.
- On **Windows**: a two-tier model:
  - **Elevated mode**: dedicated local users (`CodexSandboxOffline`, `CodexSandboxOnline`), Windows Firewall rules block outbound network for the offline user, write-restricted tokens via `CreateProcessAsUser`, ACL grants on the working directory.
  - **Unelevated mode**: write-restricted token derived from current user with synthetic SIDs; environment-level offline controls (proxy env override, stub executables for network tools). Weaker boundary — ACL setup is expensive and network isolation requires firewall rules.
- **Cloud runs**: disposable containers with diff-based output; VM isolation with egress allowlists.

**Profiles:**
- `:read-only` — no writes, no network, read anywhere
- `:workspace` — read/write in project dir, network disabled by default, `.git/` and `.codex/` always read-only
- `:danger-no-sandbox` — full filesystem and network access

**Guardian Auto-Review**: routes approval requests through a reviewer sub-agent evaluating data exfiltration, credential probing, persistent security weakening, and destructive actions. Low-risk proceeds; critical-risk denied outright.

### 1.2 Cursor

Cursor runs terminal commands in a **restricted environment** that blocks unauthorized file access and network activity. The agent harness makes models "sandbox-aware" by:
- Updating Shell tool descriptions to explain sandbox constraints
- Surfacing sandbox constraint failures in tool results
- Recommending escalation when commands hit sandbox boundaries

**Platform implementation:**
- **macOS**: Seatbelt via `sandbox-exec` with a dynamically generated profile based on workspace-level settings, admin policies, and `.cursorignore`.
- **Linux**: Landlock + seccomp directly (kernel 6.2+ with Landlock v3 support). Workspaces are mapped into an overlay filesystem; ignored files are overwritten with Landlocked copies that cannot be read or modified.
- **Windows**: runs the Linux sandbox inside WSL2. No native Windows boundary yet.

**Run Modes (stacked layers):**
1. **Allowlist** — listed actions auto-run, everything else prompts
2. **Auto-review** (recommended default) — allowlisted calls run immediately; shell commands run sandboxed when possible; non-sandboxable calls go to an LLM classifier (small Cursor-managed model) that returns `allow`, `try-something-different`, or `ask-the-human`
3. **Run Everything** — no sandbox, no classifier, no prompts

**Key execution details:**
- Sandboxing is a layer **under** Run Modes for shell commands — it controls *where* a command runs, not *whether* the mode uses the classifier.
- Commands that cannot run in the sandbox (writes outside workspace, privileged operations, network needs) escalate to the classifier or user prompt.
- `permissions.json` uses natural-language `allow_instructions` and `block_instructions` for the classifier — probabilistic at the boundary.
- `sandbox.json` controls network domains and filesystem paths.

### 1.3 Kilo

Kilo explicitly states: **"The permission system exists as a UX feature to help users stay aware of what actions the agent is taking — it prompts for confirmation before executing commands, writing files, etc. However, it is not designed to provide security isolation."**

- No default sandbox. Kilo runs as the IDE user / shell user, inheriting all parent permissions.
- For true isolation, the recommended path is Docker or VM.
- The optional **Kilo sandbox** adds an OS boundary around agent tools:
  - Limits writes to explicitly allowed locations
  - Blocks outbound network access from model-originated commands by default
  - Does **not** restrict filesystem reads
  - Applies to child processes (package installers, build scripts)
- `network "deny"` controls outbound network while filesystem confinement is active.
- `sandbox.allowed_hosts` allows configured DNS hosts and ports for proxy traffic.

---

## 2. Permission Models

### 2.1 Codex

- **Approval policy**: `untrusted` (only known-safe read commands auto-approved), `on-request` (model decides when to ask), `never` (never asks)
- **Granular approval**: per-category control (`sandbox_approval`, `rules`, `mcp_elicitations`, `request_permissions`)
- **Shell environment policy**: strips credentials from subprocess environments, overrides proxy-related env vars
- **Rules**: allow/block patterns in `config.toml` that can block specific commands (e.g., `npm publish` → ask)

### 2.2 Cursor

- **permissions.json**: natural-language allow/block instructions for the Auto-review classifier
- **sandbox.json**: deterministic filesystem and network controls
- **hooks.json**: fail-open hook gates (`beforeShellExecution`) that catch known dangerous patterns regardless of Run Mode or sandbox state. Deliberately `failClosed: false` — infrastructure failure must never look like a security block.
- **Team-admin layer**: team settings take precedence over individual and project configuration
- **MCP allowlist**: pre-approve specific MCP tools

### 2.3 Kilo

- **kilo.jsonc**: `permission` key with `allow` / `ask` / `deny` per tool
- Granular bash permissions with glob pattern matching:
  ```json
  "permission": {
    "bash": "ask",
    "read": "allow",
    "edit": "allow",
    "webfetch": "ask",
    "external_directory": {
      "~/projects/shared-lib/**": "allow"
    }
  }
  ```
- Default denies writes outside the working directory
- Skill allowlist for production deployments

### 2.4 Harbor (Current)

Harbor already has a well-structured permission model:

- **PermissionRuleset**: ordered list of `PermissionRule(Permission, Pattern, Action)` where Action is `Allow | Ask | Deny`
- Rules are pre-sorted by specificity (descending) and Deny-first at construction time
- **Default ruleset**:
  - `read`, `glob`, `grep`, `ls`, `tree`, `ripgrep`, `notebook`, `patch` → Allow
  - `write` to `src/*` → Allow; `write` elsewhere → Ask
  - `edit` to `src/*` → Allow; `edit` to `*.env*` → Deny; `edit` elsewhere → Ask
  - `bash`: safe commands (`ls`, `cat`, `grep`, `rg`, `find`, `git status/diff/log`) → Allow; `rm -rf /` and `sudo *` → Deny; everything else → Ask
  - `webfetch`, `mcp` → Ask
  - `task` → Allow
- **BashArgMatcher**: detects destructive commands, shell metacharacters, and computes deny-match targets for every bash invocation
- **PathGuardTools**: prevents path traversal escapes and absolute paths from matching glob Allow rules
- **Per-agent rulesets**: `code`, `plan`, `explore` agents each have own permissions; sub-agent permissions do not leak from parent
- **Merge**: agent + session permissions are merged; user-supplied rules take precedence

**Gap**: Harbor's permission model is purely application-level. There is no OS-level sandbox enforcement.

---

## 3. Audit Logging

### 3.1 Codex

- **JSONL session files**: every turn, tool call, and tool result is persisted
- Event sourcing: `agent_start` → `turn_start` → `message_*` → `tool_execution_*` → `turn_end` → `agent_end`
- Configurable compaction with anchored-summary strategy
- Session archiving (v0.136+)
- `codex exec --json` outputs machine-readable results for scripting
- Shell environment policy + approval policy create a de-facto audit trail

### 3.2 Cursor

- **No native per-action audit log** — the classifier's allows "vanish into the session"
- Session history exists in the IDE, but there is no structured, exportable per-action ledger
- Cloud agents: control is the VM boundary + PR review; no per-action record

### 3.3 Kilo

- Session persistence via JSONL or memory stores (depending on Harbor's implementation when integrated)
- Snapshot system for undo/revert at `~/.local/share/kilo/snapshot/`
- Telemetry opt-in via OTLP

### 3.4 Harbor

Harbor has strong audit foundations:
- **IEventBus pub/sub**: all agent events are typed (`AgentStartEvent`, `ToolExecutionStartEvent`, `ToolExecutionEndEvent`, etc.)
- **JSONL / Sqlite / Memory** session stores
- **Event-driven architecture**: renderers, loggers, and plugins subscribe to `AgentEvent`s
- **Compaction service**: anchored-summary with configurable reserve tokens
- **Doom-loop detection**: repeated identical tool calls trigger user prompt
- **Gap**: no structured, queryable audit log of permission decisions (allow/ask/deny with timestamps, user responses, and tool call context)

---

## 4. Network Isolation

### 4.1 Codex

- **Binary on/off** in workspace-write mode (`network_access = false` / `true`)
- No domain-level filtering in Seatbelt; Linux `seccomp` blocks specific network syscalls
- Firewall-based outbound controls in Windows elevated sandbox
- Cloud: egress allowlists per user/team/environment
- `web_search = "cached"` uses OpenAI-maintained index rather than live fetch (reduces prompt injection risk)

### 4.2 Cursor

- **Network blocked by default** in sandbox
- `sandbox.json` `networkPolicy`:
  - `default: "deny"` — network limited to domains in allowlist
  - `default: "allow"` — all outbound permitted
  - Deny list always beats allow list
  - Private/RFC 1918 addresses and cloud metadata endpoints (`169.254.169.254`) blocked by default to prevent SSRF
- `sandbox.json + Defaults` mode adds Cursor's built-in defaults for common package managers and language tools
- Team-admin dashboard can enforce network allowlists/denylists organization-wide
- Cloud Agents: Tailscale / Cloudflare Tunnel for private network access; three modes (Allow all / Default + allowlist / Allowlist only)

### 4.3 Kilo

- Optional sandbox: `network "deny"` blocks outbound network from sandboxed commands
- `sandbox.allowed_hosts` permits explicit DNS hosts and ports
- Network restriction applies even when filesystem confinement is active

### 4.4 Harbor

- **No network isolation currently**. The `webfetch` tool exists but has no network policy layer.
- **Gap**: Harbor needs a network policy framework (default-deny with explicit allowlists, SSRF protection, per-tool network permissions)

---

## 5. Filesystem Access Controls

### 5.1 Codex

- **Workspace-write**: read anywhere, write only to workspace + `writable_roots` + temp
- **Protected paths always read-only**: `.git/`, `.agents/`, `.codex/` regardless of mode
- **Read-only**: no filesystem writes permitted (including `/tmp`)
- Custom profiles can define `writable_roots` and per-path access levels (`write`, `read`, `none`)
- Path traversal guards in permission rules

### 5.2 Cursor

- **Workspace files**: read and write inside workspace by default
- **Protected paths (hardcoded, cannot be weakened)**:
  - `.cursor/*.json`, `.cursor/**/*.json`, `.cursor/.workspace-trusted`
  - `.claude/*.json`, `.claude/**/*.json`
  - `.vscode/**`
  - `.git/**`, `.git/config`, `.git/info/attributes`
  - `.cursorignore`
  - SSL certificate paths and `~/.ssh` (always readable)
- `.cursorignore` hides files from the agent entirely; ignored files are mapped to Landlocked copies on Linux
- `additionalReadwritePaths` / `additionalReadonlyPaths` in `sandbox.json`
- `disableTmpWrite` removes default temp directory write access
- `enableSharedBuildCache` redirects build caches to shared tmpdir

### 5.3 Kilo

- Optional sandbox: writes only to explicitly allowed locations
- Blocks writes outside the working directory by default
- `external_directory` permission allows specific external paths via glob patterns
- No enforced read restrictions in sandbox

### 5.4 Harbor

- **Application-level path guards**:
  - `PathGuardTools` prevents `..` traversal and absolute paths from matching glob Allow rules
  - `HasUnsafePathShape` blocks rooted paths and traversal segments
  - Only literal patterns (no glob metacharacters) can allow unsafe paths
- **Default ruleset**:
  - `write` to `src/*` → Allow; `write` elsewhere → Ask
  - `edit` to `*.env*` → Deny
- **No OS-level filesystem enforcement** — writes are controlled only by the permission service and tool implementations

---

## 6. What Harbor Should Adopt

### 6.1 Immediate Priorities (Low Effort, High Value)

| # | Adoption | Rationale | Effort |
|---|----------|-----------|--------|
| 1 | **Structured audit log of permission decisions** | Cursor lacks this; Codex has JSONL but not queryable. Harbor's `IEventBus` already emits all events — add a persistent `PermissionDecisionLogged` event with timestamp, agent, tool, args hash, action, and user response. | Low |
| 2 | **Network policy layer** | No other major tool leaves network fully open by default. Add `network` as a permission category (like `exec`, `read`, `write`) with default-deny. | Medium |
| 3 | **Protected paths hardcoding** | Adopt Cursor's approach: hardcode deny-write on `.git/**`, `.harbor/**`, config files, and credential paths. | Low |
| 4 | **Shell metacharacter + destructive command detection** | Harbor already has `BashArgMatcher`. Extend it to cover PowerShell AST classification (relevant for Windows). | Medium |

### 6.2 Medium-Term (Medium Effort, Transformative)

| # | Adoption | Rationale | Effort |
|---|----------|-----------|--------|
| 5 | **OS-level sandbox abstraction** | Adopt Codex's platform-specific approach but abstract behind an interface so Harbor can support Seatbelt (macOS), Landlock+seccomp (Linux), and restricted tokens (Windows). Start with Linux (Landlock is upstream in kernel). | High |
| 6 | **Fail-closed hook seam** | Cursor deliberately uses fail-open hooks. Harbor should offer both: fail-open for UX hooks, but a **fail-closed security hook layer** that runs before the sandbox and permission service. | Medium |
| 7 | **Named permission profiles** | Codex's `:read-only`, `:workspace`, `:danger-no-sandbox` profile pattern is cleaner than Harbor's flat rulesets. Introduce `PermissionProfile` with bundled filesystem + network + approval settings. | Medium |
| 8 | **Sub-agent permission isolation hardening** | Harbor already isolates child agent permissions from parent. Add resource limits (CPU, memory, wall clock) to sub-agents via `task` tool. | Medium |

### 6.3 Long-Term (High Effort, Defense in Depth)

| # | Adoption | Rationale | Effort |
|---|----------|-----------|--------|
| 9 | **Out-of-process tool execution** | Move `bash`, `webfetch`, and `mcp` tool execution to a separate, sandboxed process communicating over IPC/stdio. The main Harbor process never directly executes untrusted code. | High |
| 10 | **Container/VM fallback for hostile workloads** | When OS sandbox is unavailable or insufficient (e.g., Windows without WSL2), fall back to a lightweight container (Docker/Podman) or Firecracker microVM. | High |
| 11 | **Credential filtering in subprocess environments** | Adopt Codex's `shell_environment_policy`: strip `*KEY`, `*SECRET`, `*TOKEN`, `*PASSWORD` from environments of spawned processes. | Low |
| 12 | **Secrets scanning before tool execution** | Scan tool args (especially `bash`, `webfetch`, `mcp`) for credential patterns and warn/deny. Complement existing `*.env*` deny rules. | Medium |

### 6.4 What NOT to Adopt

| Anti-pattern | Why | Source |
|--------------|-----|--------|
| **Natural-language permission instructions as enforcement** | Cursor's `permissions.json` uses LLM-classified natural language. Probabilistic judgments are not deterministic security boundaries. | Cursor |
| **Cloud agents with no per-action record** | Cursor's cloud agents auto-run everything inside a VM with no per-action audit log. Harbor should never skip policy for convenience. | Cursor |
| **Permission system as a sandbox replacement** | Kilo explicitly warns that permissions are UX, not isolation. Harbor must not conflate the two. | Kilo |
| **Fail-closed hooks without sandbox fallback** | If hooks fail open (as Cursor's do), the sandbox must still catch what slips through. Conversely, if the sandbox is unavailable, fail-closed hooks must block. | Cursor, Codex |

---

## 7. Comparative Matrix

| Axis | Codex CLI | Cursor | Kilo | Harbor (current) |
|------|-----------|--------|------|------------------|
| **OS sandbox** | Seatbelt / Bubblewrap+Landlock / Restricted Token | Seatbelt / Landlock+seccomp / WSL2 | None (optional wrapper) | None |
| **Permission model** | Allow/Ask/Deny + profiles | Allowlist + classifier + sandbox | Allow/Ask/Deny per tool | Allow/Ask/Deny per tool per glob |
| **Network isolation** | Binary on/off; seccomp blocks syscalls | Default-deny domain allowlists + SSRF blocks | Optional deny + allowed_hosts | None |
| **Filesystem controls** | Writable roots, protected paths always RO | Hardcoded deny-write + sandbox.json | Writes to allowed locations only | Glob-based path guards |
| **Audit logging** | JSONL sessions + event sourcing | None (classifier vanishes) | Session snapshots | EventBus + session stores |
| **Sub-agent isolation** | Yes (same sandbox + approval) | Yes (same Run Mode) | Limited | Yes (own ruleset per agent) |
| **Secrets filtering** | Yes (env stripping) | Partial (~/.ssh readable) | No | Partial (*.env* deny) |
| **Fail mode** | Fail-closed (sandbox kills) | Fail-open hooks, sandbox fallback | Fail-open (no sandbox) | Fail-open (ask user) |

---

## 8. Recommended Reading Order for Harbor Team

1. **Cursor's sandbox blog post** — `cursor.com/blog/agent-sandboxing` (how they made models sandbox-aware)
2. **Codex Windows sandbox deep dive** — `codex.danielvaughan.com/2026/05/14/...` (restricted tokens, synthetic SIDs, four-layer architecture)
3. **Codex command safety** — `codex.danielvaughan.com/2026/06/06/...` (defense in depth from shell injection to sandbox)
4. **Kilo SECURITY.md** — explicit threat model and "not a sandbox" disclaimer
5. **Harbor's PermissionRuleset.cs** — already has sophisticated glob evaluation, path guards, and destructive command detection

---

## 9. Conclusion

The ecosystem is converging on a **three-layer model**:

1. **Deterministic policy** (permissions + sandbox) — the hard boundary
2. **Probabilistic review** (LLM classifier / auto-review) — the fatigue reducer
3. **Audit trail** (structured logging of every decision) — the accountability layer

Harbor has a strong foundation in layer 1 (permissions) and layer 3 (event bus). The critical gaps are **OS-level sandbox enforcement** and **network isolation**. The recommended path is:

- **Now**: add structured permission decision logging, network permission category, protected paths hardcoding, and credential environment filtering.
- **Next**: introduce named permission profiles and an OS-sandbox abstraction layer (start with Linux/Landlock).
- **Later**: out-of-process tool execution with IPC, and container/VM fallback for hostile workloads.

Do not adopt Cursor's natural-language classifier as a security boundary, and do not adopt Kilo's "permissions are UX" stance as an excuse to skip OS-level isolation.
