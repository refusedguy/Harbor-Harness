# Harbor.Hooks

The user-hook runner: executes plain shell commands from `~/.harbor/hooks.json`
at six lifecycle points and folds their JSON verdicts into a single decision.
Application-layer sibling of `Harbor.Registries` — a verbatim extraction of
`Harbor.Application/Hooks` (#1613) so the hooks pipeline can be built, packed,
and AOT-published without pulling the whole application layer it once rode on.

## What's in here

| File | Type | Responsibility |
|------|------|----------------|
| `HookEvents.cs` | `static class HookEvents` | The six event-point names (`PreToolUse`, `PostToolUse`, `UserPromptSubmit`, `SessionStart`, `PreCompact`, `SessionEnd`), split into **gates** (verdict decides whether the work happens) and **advisory** points (verdict logged and discarded). |
| `HookConfig.cs` | `HookConfig`, `HookEntry`, `HookFileConfig` | Loads and caches `hooks.json` (re-reads when the file mtime changes), resolves per-event entries, matcher/ timeout validation. Default path: `~/.harbor/hooks.json`. |
| `IHookRunner.cs` | `IHookRunner`, `HookVerdict`, `HookPayload`, `HookVerdictDto`, `HookDecision` | The consumer-facing contract: five `Run*Async` methods plus the payload/verdict shapes hooks exchange. |
| `HookRunner.cs` | `HookRunner : IHookRunner` | The executor: spawns each matched hook command on a shell with a JSON payload on stdin, enforces the per-hook timeout, merges verdicts (first deny wins, later deny overrides ask, edits compose), fail-closed on the gates. |
| `HookJsonContext.cs` | `JsonSerializerContext` | Source-generated JSON for the payload/verdict DTOs — AOT-safe, no reflection. |
| `GlobalUsings.cs` | — | Narrows to `System.Text.Json` + `Harbor.Abstractions.Models` (`ToolResult`). |

## Public API

Consumers bind `Harbor.Application.Hooks` types — the namespace survives the
extraction (verbatim slice-1 move, same convention as #1464/#1475/#1480), so
existing usings keep compiling:

```csharp
public interface IHookRunner
{
    Task<HookVerdict> RunPreToolUseAsync(string toolName, JsonElement args, string sessionId, CancellationToken ct = default);
    Task RunPostToolUseAsync(string toolName, JsonElement args, ToolResult result, string sessionId, CancellationToken ct = default);
    Task RunSessionEndAsync(string sessionId, CancellationToken ct = default);
    Task RunSessionStartAsync(string sessionId, string? agent = null, string? model = null, CancellationToken ct = default);
    Task<HookVerdict> RunUserPromptSubmitAsync(string sessionId, string prompt, string? agent = null, string? model = null, CancellationToken ct = default);
    Task RunPreCompactAsync(string sessionId, string? agent = null, string? model = null, CancellationToken ct = default);
}

public enum HookDecision { Allow, Deny, Ask, Edit }
public sealed record HookVerdict(HookDecision Decision, string? Reason, JsonElement? EditedArgs);
public sealed record HookPayload(string Event, string? Tool, JsonElement? Args, string? Result, string SessionId, ...);
public sealed class HookConfig(string? path = null);
public static class HookEvents { public const string PreToolUse = "PreToolUse"; /* + 5 more */ }
public sealed class HookRunner : IHookRunner;
```

Also: `HookRunner.DefaultHookTimeout` (30 s) / `MaxHookTimeout` (5 min) and
`HookConfig.GetDefaultPath()` / `Matches(matcher, toolName)` / `GetHooks(eventName, toolName)`.

## Wiring

Registered by the default DI graph in
[`Harbor.Hosting/Modules/CoreModule.cs`](../Harbor.Hosting/Modules/CoreModule.cs)
(`AddSingleton<IHookRunner>`, optional consumer: the tool dispatcher resolves
it with `GetService`, so a host that omits the registration keeps the
hooks-free path). No `HARBOR_*` env var — activation is purely the presence of
entries in `~/.harbor/hooks.json` (`HookConfig.GetDefaultPath()`); an absent or
empty file is a no-op, not an error. No settings beyond the runner
constructor(`configPath`, `logger`, `defaultTimeout`).

## Usage

Minimal `~/.harbor/hooks.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "bash",
        "command": "python3 ~/.harbor/hooks/block-rmrf.py",
        "timeoutSeconds": 10
      }
    ]
  }
}
```

Consumer side (how the dispatcher calls the runner):

```csharp
HookVerdict verdict = await hookRunner.RunPreToolUseAsync("bash", args, sessionId, ct);
if (verdict.Decision is HookDecision.Deny)
    return ToolResult.Error(verdict.Reason ?? "hook denied the call");
if (verdict.EditedArgs is { } edited)
    args = edited.Value; // narrowing hooks compose: each sees the prior edits
```

A hook reads the `HookPayload` serialized on **stdin**, writes `{"decision":
"allow" | "deny", "reason": "..."}` (or `edit` + `editedArgs`, gates only) on
**stdout**; exit status alone is advisory-only, a non-zero exit never denies.

## Dependencies

- **References:** `Harbor.Abstractions.Contracts` (for `ToolResult` only) plus
  `Microsoft.Extensions.Logging.Abstractions`. No Harbor facade edge — the
  Hooks code resolves no facade-level type, which the allowed-set liveness
  rule in the architecture tests pins.
- **Referenced by:** `Harbor.Application`, `Harbor.Hosting`. Never on
  `Harbor.Plugins.*`, `Harbor.Providers.*`, `Harbor.Storage.*`, `Harbor.Tui.*`,
  or any app. Enforced by `tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`
  (Application layer; the only Harbor edge is → Domain Contracts).

## Limits

- Plain commands, not plugins: no Roslyn compilation, no `Assembly.Load`, no
  plugin interface family — deliberately (AOT-safe by construction).
- Gates fail **closed**: a hook that times out, cannot start, prints invalid
  JSON, or names an unknown decision denies the work. Advisory points log hook
  failures and can never fail the run.
- No stdin/output size ceiling beyond the 500-char stderr excerpt embedded in
  deny reasons; no hook output truncation for `PostToolUse` escape hatches —
  advisory verdicts are discarded, so oversized stdout costs only read time.
- Windows shells: commands run through the platform default shell; exotic
  hook scripts relying on POSIX-only behavior need porting (same caveat as
  the `bash` builtin tool).
