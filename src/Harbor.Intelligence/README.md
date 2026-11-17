# Harbor.Intelligence

The first leaf of the `Harbor.Intelligence` assembly (#1626, ADR-018 variant A).
It owns the read-only git probes that used to live in `Harbor.Application/Git`:
a process-backed `IGitQuery` and the pure parser for `git worktree list
--porcelain`. Application-layer sibling of `Harbor.Registries` and
`Harbor.Hooks`, extractable and packable on its own.

## Layer

Application. The layer matrix row is
`tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`.

## Public API

Consumers bind `Harbor.Application.Git` types. The namespace survives the
extraction (verbatim slice move), so existing usings keep compiling:

```csharp
public sealed class ProcessGitQuery(ILogger<ProcessGitQuery> logger) : IGitQuery
{
    public GitWorkspaceStatus GetStatus(string directory, CancellationToken cancellationToken = default);
    public IReadOnlyList<GitWorktreeInfo> ListWorktrees(string directory, CancellationToken cancellationToken = default);
}

public static class WorktreePorcelainParser
{
    public static IReadOnlyList<GitWorktreeInfo> Parse(string? output);
}
```

`IGitQuery`, `GitWorkspaceStatus` and `GitWorktreeInfo` are declared in
`Harbor.Abstractions/Git/IGitQuery.cs` and stay there.

## Wiring

The composition roots register the implementation against the port:

```csharp
services.AddSingleton<IGitQuery, ProcessGitQuery>();
```

That lives in `src/Harbor.Hosting/Modules/TuiModule.cs` and
`apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs`. Both hold a direct
`ProjectReference` to this assembly.

## Usage

```csharp
using Harbor.Abstractions.Git;
using Harbor.Application.Git;

IGitQuery git = new ProcessGitQuery(logger);
GitWorkspaceStatus status = git.GetStatus("/path/to/repo");
IReadOnlyList<GitWorktreeInfo> worktrees = git.ListWorktrees("/path/to/repo");
```

Every command it spawns is read-only (`rev-parse`, `status --porcelain`,
`log -1`, `worktree list --porcelain`) and both child pipes are drained for the
life of the process (#884).

## Dependencies

- `Harbor.Abstractions` — `IGitQuery`, `GitWorkspaceStatus`, `GitWorktreeInfo`.
- `Harbor.Extensions` — `StringBuilderPool` for the per-spawn stdout buffer.
- `Microsoft.Extensions.Logging.Abstractions` — `ILogger<T>` and
  `[LoggerMessage]` delegates.
- Referenced by `Harbor.Hosting`, `Harbor.App.Avalonia` and
  `Harbor.Application.Tests` (pinned by `tests/Harbor.Architecture.Tests`).

## Known limitations

- This is a partial slice of #1626. Skills, Diagnostics, Filesystem,
  Attachments, ProviderHealthCheck and Telemetry still live in
  `Harbor.Application` and move into this assembly in later slices.
- `GetStatus` and `ListWorktrees` are synchronous: the port is sync and one
  caller paints a UI badge on the render thread.
- A hung git is killed at 3 seconds and reported as "no workspace", not as an
  error.

## See also

- [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) §2 — layer matrix.
- [`docs/adr/ADR-018-harbor-application-split-variant-a.md`](../../docs/adr/ADR-018-harbor-application-split-variant-a.md) — the split decision.
- [`docs/SEAM_GRAPH.md`](../../docs/SEAM_GRAPH.md) — the `IGitQuery` seam.
