#!/usr/bin/env bash
set -euo pipefail

# ============================================================
# Create recommended issues for refusedguy/Harbor-Harness
# Requires: gh CLI authenticated (gh auth login)
# Usage:
#   ./create-harbor-issues.sh
#   ./create-harbor-issues.sh --repo refusedguy/Harbor-Harness
#   ./create-harbor-issues.sh --dry-run          # только показать, ничего не создавать
# ============================================================

REPO="refusedguy/Harbor-Harness"
DRY_RUN=0

if [[ $# -gt 0 ]]; then
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --repo)
        if [[ -z "${2:-}" ]]; then
          echo "error: --repo requires a value" >&2
          exit 1
        fi
        REPO="$2"
        shift 2
        ;;
      --dry-run) DRY_RUN=1; shift ;;
      -h|--help)
        echo "Usage: $0 [--repo owner/repo] [--dry-run]"
        exit 0
        ;;
      *) echo "Unknown arg: $1"; exit 1 ;;
    esac
  done
fi

echo "→ Target repo: $REPO"
echo "→ Dry-run: $DRY_RUN"
echo

# ---------- preflight ----------
if ! command -v gh >/dev/null 2>&1; then
  echo "error: gh CLI not found. Install: https://cli.github.com" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "error: gh not authenticated. Run: gh auth login" >&2
  exit 1
fi

# ---------- labels (создаём, если нет) ----------
ensure_label() {
  local name="$1" color="$2" desc="$3"
  if gh label list --repo "$REPO" --limit 200 --json name --jq '.[].name' | grep -Fxq "$name"; then
    echo "  label exists: $name"
  else
    if [[ $DRY_RUN -eq 1 ]]; then
      echo "  [dry-run] would create label: $name"
    else
      if ! gh label create "$name" --repo "$REPO" --color "$color" --description "$desc" 2>/dev/null; then
        echo "  warning: failed to create label $name (may already exist)"
      else
        echo "  created label: $name"
      fi
    fi
  fi
}

echo "Ensuring labels..."
ensure_label "priority: high"   "B60205" "High priority"
ensure_label "priority: medium" "D93F0B" "Medium priority"
ensure_label "priority: low"    "0E8A16" "Low priority"
ensure_label "type: feature"    "1D76DB" "New feature"
ensure_label "type: tech-debt"  "FBCA04" "Refactor / technical debt"
ensure_label "type: bug"        "D73A4A" "Bug / flaky test"
ensure_label "type: test"       "C5DEF5" "Testing improvements"
ensure_label "type: docs"       "0075CA" "Documentation"
ensure_label "area: sub-agents" "5319E7" "Sub-agents / TaskTool"
ensure_label "area: mcp"        "5319E7" "MCP"
ensure_label "area: providers"  "5319E7" "LLM providers"
ensure_label "area: sessions"   "5319E7" "Sessions"
ensure_label "area: tui"        "5319E7" "TUI / ConsoleEx"
ensure_label "area: architecture" "5319E7" "Architecture / layering"
ensure_label "area: ci"         "5319E7" "CI / Actions"
ensure_label "good first issue" "7057FF" "Good for newcomers"
echo

# ---------- helper ----------
create_issue() {
  local title="$1"
  local body="$2"
  shift 2
  local labels=("$@")

  local label_args=()
  for l in "${labels[@]}"; do
    label_args+=(--label "$l")
  done

  if [[ $DRY_RUN -eq 1 ]]; then
    echo "[dry-run] $title"
    echo "         labels: ${labels[*]}"
    echo
    return
  fi

  gh issue create \
    --repo "$REPO" \
    --title "$title" \
    --body "$body" \
    "${label_args[@]}"
  echo "✓ created: $title"
  echo
}

# ============================================================
# HIGH PRIORITY
# ============================================================

create_issue \
  "Implement real sub-agent execution for TaskTool" \
  "$(cat <<'EOF'
## Context
`TaskTool` currently only validates the sub-agent and fails with an explicit "not implemented" error.
See `src/Harbor.Tools.Builtin/Tools/Task/TaskTool.cs` and ROADMAP v0.5.0.

## Goal
Make `task` tool actually run sub-agents.

## Acceptance criteria
- [ ] Sub-agent runs in an **isolated context** with its own session
- [ ] Result is returned to the parent agent as tool output
- [ ] Support builtin agents: `explore`, `plan`, `code` (and custom ones via registry)
- [ ] Own `PermissionRuleset` per sub-agent is respected
- [ ] Parent can cancel / timeout the child
- [ ] Unit + at least one E2E test covering the happy path
- [ ] Document the contract in `docs/TOOLS_CATALOG.md` and AGENTS.md

## References
- ROADMAP.md → v0.5.0 — Sub-agent execution
- `IAgentRegistry`, `PermissionRuleset`
EOF
)" \
  "priority: high" "type: feature" "area: sub-agents"

create_issue \
  "Decompose remaining god-objects (OpenAI/Anthropic clients, HarborConfig, HostBuilder, SessionManager)" \
  "$(cat <<'EOF'
## Context
Post-R31 tech-debt backlog still has several large classes that mix concerns.

## Targets
| File | ~Lines | Suggested split |
|------|--------|-----------------|
| `Harbor.Providers.OpenAI/OpenAILlmClient.cs` | 656 | `OpenAiSseParser` + `OpenAiEventMapper` |
| `Harbor.Providers.Anthropic/AnthropicLlmClient.cs` | 562 | same pattern |
| `Harbor.Application/Configuration/HarborConfig.cs` | 492 | per-section records (`ProviderConfig`, `ToolConfig`, `UiConfig`…) |
| `apps/Harbor.App.Cli/Hosting/HostBuilder.cs` | 644 | per-concern registrars (mirror Avalonia `Hosting/`) |
| `apps/Harbor.App.Avalonia/Services/SessionManager.cs` | 492 | move orchestration toward `Ui.Framework` |

## Acceptance criteria
- [ ] Each extraction has a clear single responsibility
- [ ] Public contracts stay stable (or are updated with a migration note)
- [ ] Architecture tests still pass
- [ ] No new warnings / analyzers violations
- [ ] Related unit tests updated

## References
- ROADMAP.md → Tech Debt — God-objects still pending decomposition
- PROJECT_STATUS.md → Top 3 next steps
EOF
)" \
  "priority: high" "type: tech-debt" "area: architecture" "area: providers"

create_issue \
  "Fix known flaky / failing tests (Avalonia headless + IPC on Linux)" \
  "$(cat <<'EOF'
## Known failures
### Avalonia 12 headless
- `MarkdownRenderer_SetMarkdown_DoesNotThrow`
- `CodeBlock_Default_Code_IsEmpty`
- `TypewriterStreamingText_CanSet_Text`
Fail with `InvalidOperationException: Stack empty` in `SetInheritanceParent` / `AvaloniaPropertyDictionaryPool`.
Likely Avalonia.Headless issue, but needs confirmation + mitigation.

### IPC
- 8/35 tests in `Harbor.Ipc.Tests` fail on Linux (named-pipe disposal race). Pass on Windows.

### Occasional flakes
- `ChatView_Inflates`
- `TryGet_ReturnsNullForUnregistered`

## Acceptance criteria
- [ ] Root-cause investigation documented
- [ ] Tests either fixed or explicitly marked as known-flaky with retry/skip policy
- [ ] CI does not report them as hard failures without a clear label
- [ ] PROJECT_STATUS.md / ROADMAP.md updated

## References
- docs/PROJECT_STATUS.md → Known broken / pre-existing
- docs/ROADMAP.md → Testing debt
EOF
)" \
  "priority: high" "type: bug" "type: test" "area: tui" "area: ci"

create_issue \
  "MCP: add HTTP/SSE transports, resources, prompts, OAuth, lazy reconnect" \
  "$(cat <<'EOF'
## Context
Stdio MCP client is already implemented (own JSON-RPC, no NuGet dependency).
Missing pieces from ROADMAP v0.6.0.

## Scope
- [ ] HTTP transport
- [ ] SSE transport
- [ ] MCP resources exposed as `read_mcp_resource` tools
- [ ] MCP prompts as slash-commands
- [ ] OAuth for MCP servers
- [ ] Lazy connect + reconnect on failure

## Acceptance criteria
- [ ] Config schema in `~/.harbor/mcp.json` extended where needed
- [ ] Source-generated / AOT-safe serialization kept
- [ ] Tests for new transports (at least happy-path + reconnect)
- [ ] Docs updated (TOOLS_CATALOG / PLUGIN / GETTING_STARTED)

## References
- ROADMAP.md → v0.6.0 — MCP Integration
- `src/Harbor.Tools.Builtin/Tools/Mcp/`
EOF
)" \
  "priority: high" "type: feature" "area: mcp"

# ============================================================
# MEDIUM PRIORITY
# ============================================================

create_issue \
  "Skills system (SKILL.md discovery + skill tool + system-prompt injection)" \
  "$(cat <<'EOF'
## Goal (ROADMAP v0.7.0)
- `SKILL.md` format with YAML frontmatter
- Discovery from `~/.harbor/skills/` and project `.harbor/skills/`
- `skill` tool to load skill content
- `<available_skills>` XML block in system prompt
- CLI: `harbor skill install/list`

## Acceptance criteria
- [ ] Clear SKILL.md schema documented
- [ ] Discovery is deterministic and respects trust boundaries
- [ ] System prompt builder injects available skills
- [ ] Unit tests + one E2E example
EOF
)" \
  "priority: medium" "type: feature"

create_issue \
  "LSP integration (diagnostics / references / definition tools)" \
  "$(cat <<'EOF'
## Goal (ROADMAP v0.7.0)
- LSP client via `OmniSharp.Extensions.LanguageServer.Client`
- 10+ builtin language servers (TypeScript, Python, Go, Rust, C# …)
- Auto-spawn on file open
- Tools: `diagnostics`, `references`, `definition`
- LSP-aware `read` / `edit` (inject diagnostics)

## Acceptance criteria
- [ ] At least 2–3 language servers wired end-to-end
- [ ] Tools appear in the registry and work in the agent loop
- [ ] Graceful degradation when a language server is missing
- [ ] Docs + examples
EOF
)" \
  "priority: medium" "type: feature"

create_issue \
  "Session polish: /tree, branch summaries, JSONL import/export, rename" \
  "$(cat <<'EOF'
## Already done
- Session branching (`harbor sessions fork`)
- Snapshot/revert
- Session search

## Still missing (ROADMAP v0.8.0)
- [ ] `/tree` slash-command for branch navigation
- [ ] Branch summaries (LLM-generated on branch switch)
- [ ] JSONL import/export
- [ ] Session rename (ISessionStore metadata-update API)

## Acceptance criteria
- [ ] All four items implemented or explicitly deferred with reason
- [ ] CLI + TUI surfaces updated
- [ ] Tests for fork/tree/rename paths
EOF
)" \
  "priority: medium" "type: feature" "area: sessions"

create_issue \
  "Increase E2E coverage (component tests, VLM, load tests, plugin integration)" \
  "$(cat <<'EOF'
## Current state
~12 E2E tests. ROADMAP calls for significantly more.

## Desired
- [ ] 33+ component tests with VLM content verification where useful
- [ ] Screenshot-diff tests for key UI components
- [ ] Load tests for concurrent multi-session scenarios
- [ ] Integration tests for plugin compilation against real-world plugin sources (not only hello-world)
- [ ] Better coverage of ConsoleEx PTY + Avalonia headless paths

## Acceptance criteria
- [ ] Coverage numbers updated in PROJECT_STATUS / ROADMAP
- [ ] New tests are stable in CI
EOF
)" \
  "priority: medium" "type: test"

create_issue \
  "Resolve circular dependency around ICommonConfigReader / Desktop.Abstractions" \
  "$(cat <<'EOF'
## Problem
`ICommonConfigReader` lives in Ui.Framework because Ui.Framework cannot reference Desktop.Abstractions
(Desktop.Abstractions → Terminal.Abstractions → Ui.Framework).

## Options to evaluate
1. Merge `Desktop.Abstractions` into `Ui.Framework`
2. Split `Terminal.Abstractions`
3. Keep the narrow interface but document the boundary more clearly

## Acceptance criteria
- [ ] Chosen approach implemented
- [ ] Architecture tests green
- [ ] No new layering violations
- [ ] Decision recorded in DECISIONS.md
EOF
)" \
  "priority: medium" "type: tech-debt" "area: architecture"

# ============================================================
# LOW / POLISH
# ============================================================

create_issue \
  "Explore two-process architecture (NativeAOT core + JIT UI over UDS)" \
  "$(cat <<'EOF'
## Goal (ROADMAP v0.9.0)
- Core (NativeAOT) + TUI (JIT) in separate processes
- NDJSON over Unix Domain Socket
- Late-attach with scrollback replay
- Multi-client support (terminal + IDE + web)
- `harbor serve` (headless) + `harbor tui` (attach)

## Acceptance criteria
- [ ] Spike / prototype that proves the wire protocol
- [ ] Document trade-offs and migration path
- [ ] Decision whether to pursue for v1.0
EOF
)" \
  "priority: low" "type: feature" "area: architecture"

create_issue \
  "Docs & packaging for v1.0 (DocFX, NuGet, READMEs, security & a11y audits, i18n)" \
  "$(cat <<'EOF'
## Scope (ROADMAP v1.0.0)
- [ ] API freeze for `Harbor.Abstractions`
- [ ] NuGet packages for all `Harbor.*` libraries
- [ ] Comprehensive docs site (DocFX / MdDocs)
- [ ] Per-project READMEs (currently ~32/51)
- [ ] Security audit
- [ ] Accessibility audit (WCAG 2.1 AA for Avalonia/Blazor)
- [ ] Internationalization (i18n) for UI strings

## Acceptance criteria
- [ ] Tracking checklist in the issue or a linked project board
- [ ] Clear "done" definition per sub-item
EOF
)" \
  "priority: low" "type: docs"

create_issue \
  "Killer features backlog (tabs, worktree palette, image preview, STT, word-diff…)" \
  "$(cat <<'EOF'
From competitor audit (`docs/KILLER_FEATURES.md`), not started:

- [ ] Tab-strip with drag-reorder + close-gesture
- [ ] Worktree jump palette (Cmd-J / Ctrl+J)
- [ ] Agent pet mascot that reacts to agent state
- [ ] Image preview inline in chat
- [ ] Skill freshness pill
- [ ] Setup-guide progress ring + checklist
- [ ] Dictation / speech-to-text input
- [ ] Browser/markup overlay for screenshots
- [ ] Intra-line word-diff highlighting (currently line-level only)

Use this issue as an umbrella; spawn child issues when starting concrete work.
EOF
)" \
  "priority: low" "type: feature" "area: tui"

# ============================================================
# CI / ACTIONS
# ============================================================

create_issue \
  "CI: make whole-solution test runs reliable under Microsoft.Testing.Platform" \
  "$(cat <<'EOF'
## Problem
`dotnet test` against the whole solution currently fails / is unreliable under the Microsoft.Testing.Platform host.
Workaround: always target a single test project.

## Desired
- [ ] Either fix MTP host behaviour for this repo
- [ ] Or add a CI matrix that runs every `tests/Harbor.*.Tests` project and aggregates results cleanly
- [ ] Document the supported way in AGENTS.md / DEVELOPMENT.md

## Acceptance criteria
- [ ] CI job that proves all test projects on every PR
- [ ] Clear failure reporting (which project failed)
EOF
)" \
  "priority: medium" "type: test" "area: ci"

create_issue \
  "CI: Dependabot action/version policy + security advisories review" \
  "$(cat <<'EOF'
Recent Dependabot PRs bumped several GitHub Actions (codeql-action, cache, setup-dotnet, etc.).

## Desired
- [ ] Document version-pinning policy (major vs minor)
- [ ] Regular review cadence for action updates
- [ ] Surface / track NuGet audit advisories (NU190x) in CI
- [ ] Optional: auto-merge only for patch/minor of trusted actions

## Acceptance criteria
- [ ] Policy written (CONTRIBUTING or docs/DEVELOPMENT.md)
- [ ] CI fails or warns on high-severity advisories intentionally
EOF
)" \
  "priority: low" "type: tech-debt" "area: ci"

create_issue \
  "CI: harden demo GIF regeneration (size/quality drift detection)" \
  "$(cat <<'EOF'
`demo.yml` already regenerates GIFs on TUI changes.

## Desired
- [ ] Fail (or warn) on unexpected size / quality regression
- [ ] Keep deterministic playback (`harbor --demo` / vhs tapes)
- [ ] Document how to update golden GIFs intentionally

## Acceptance criteria
- [ ] Drift check in the workflow
- [ ] Clear instructions for maintainers
EOF
)" \
  "priority: low" "type: test" "area: ci" "area: tui"

# ============================================================
echo "Done."
if [[ $DRY_RUN -eq 1 ]]; then
  echo "(dry-run only — re-run without --dry-run to create issues)"
fi
