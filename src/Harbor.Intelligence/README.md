# Harbor.Intelligence

## What

Leaves of the `Harbor.Intelligence` assembly (#1626, ADR-018 variant A), moved
verbatim out of `Harbor.Application` with their namespaces preserved:

- **Git probes** (`Harbor.Application.Git`): a process-backed `IGitQuery` and
  the pure parser for `git worktree list --porcelain` (first leaf, #2273).
- **Provider health check** (`Harbor.Application.Providers`): the concrete
  `ProviderHealthCheck` adapter for `IProviderHealthCheck` (second leaf, #2283).
- **Default file-tree policy** (`Harbor.Application.Filesystem`): the pure
  `DefaultFileTreePolicy` adapter for `IFileTreePolicy` (third leaf).
- **Skill-freshness seeder** (`Harbor.Application.Skills`): the BCL-only
  `SkillFreshnessSeeder` + `SkillFreshnessSnapshot` pair that hashes installed
  skills against `skills-lock.json` (fourth leaf).
- **Image attachments** (`Harbor.Application.Attachments`): the magic-byte
  `ImageProbe` and the vision-aware `ImageAttachmentReader` (fifth leaf).

Application-layer sibling of `Harbor.Registries` and `Harbor.Hooks`, extractable
and packable on its own.

## Layer

Application. The layer matrix row is
`tests/Harbor.Architecture.Tests/FullLayerMatrixTests.cs`.

## Public API

Consumers bind `Harbor.Application.Git`, `Harbor.Application.Providers`,
`Harbor.Application.Filesystem`, `Harbor.Application.Skills` and
`Harbor.Application.Attachments` types. The namespaces survive the extraction
(verbatim slice move), so existing usings keep compiling:

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

public sealed class ProviderHealthCheck : IProviderHealthCheck
{
    public Task<Result<ProviderHealth>> CheckAsync(ProviderId providerId, CancellationToken cancellationToken = default);
    public static string Classify(string rawError);
}

public sealed class DefaultFileTreePolicy : IFileTreePolicy
{
    public const string DefaultIcon = "file";
    public const string CodeIcon = "file-code";
    public const string FolderIcon = "folder";

    public bool IsIgnoredDirectory(string name);
    public string IconFor(string fileName);
}

public sealed record SkillFreshnessSnapshot(string Name, string? InstalledHash, string? LockedHash);

public static class SkillFreshnessSeeder
{
    public const string SkillFileName = "SKILL.md";
    public static IReadOnlyList<SkillFreshnessSnapshot> Seed(
        string? projectSkillsDir, string? globalSkillsDir, string? lockFilePath);
}

public static class ImageProbe
{
    public const string PngMimeType = "image/png";
    public static bool TryProbe(ReadOnlySpan<byte> data, out string mimeType, out int width, out int height);
}

public sealed class ImageAttachmentReader
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public ImageAttachmentReader(IProviderRegistry? providers = null, ILogger? logger = null);
    public Task<Result<ImageAttachment>> ReadAsync(
        string path, string providerId, string modelId, CancellationToken ct = default);
}
```

`IGitQuery`, `GitWorkspaceStatus`, `GitWorktreeInfo`, `IProviderHealthCheck`,
`IFileTreePolicy` and `IProviderRegistry` are declared in `Harbor.Abstractions`
and stay there. The seeder and `ImageProbe` bind no Harbor port at all: both are
BCL-only, so no new `ProjectReference` was needed for them.

## Wiring

The composition roots register the implementation against the port:

```csharp
services.AddSingleton<IGitQuery, ProcessGitQuery>();
services.AddSingleton<IFileTreePolicy, DefaultFileTreePolicy>();
```

The git registration lives in `src/Harbor.Hosting/Modules/TuiModule.cs` and
`apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs`. `ProviderHealthCheck`
is registered in `src/Harbor.Hosting/Modules/CoreModule.cs`, and
`DefaultFileTreePolicy` in the Avalonia `ServiceRegistration.cs`. The seeder is
not DI-registered: the CLI `SkillFreshnessStartup` calls the static `Seed` and
maps the snapshots onto the shared freshness model (it reaches this assembly
transitively through `Harbor.Hosting`). The CLI `/attach` command constructs
`ImageAttachmentReader` directly in `apps/Harbor.App.Cli/Repl/` (no DI seam).
Every consumer composition root holds a direct `ProjectReference` to this
assembly.

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

- `Harbor.Abstractions` — `IGitQuery`, `GitWorkspaceStatus`, `GitWorktreeInfo`,
  `IProviderHealthCheck`, `IProviderRegistry`, `ProviderHealth`, `IFileTreePolicy`,
  `ImageAttachment` and `ModelInfo`.
- `Harbor.Extensions` — `StringBuilderPool` for the per-spawn stdout buffer.
- `Microsoft.Extensions.Logging.Abstractions` — `ILogger<T>` and
  `[LoggerMessage]` delegates.
- Referenced by `Harbor.Hosting`, `Harbor.App.Avalonia`, `Harbor.App.Cli` and
  `Harbor.Application.Tests` (pinned by `tests/Harbor.Architecture.Tests`).

## Known limitations

- This is a partial slice of #1626. Diagnostics, Telemetry and the rest of Skills
  (`SkillUpdater`) still live in `Harbor.Application` and move into this assembly
  in later slices.
- `SkillFreshnessSeeder` is BCL-only and keeps its `Harbor.Application.Skills`
  namespace. It carries a small BCL-only mirror of `Harbor.Application.FrontMatter`
  (issue #1580) because this leaf cannot take an Application edge; `FrontMatter.cs`
  and `SkillUpdater.cs` refer to it by name in `<c>` docs rather than a `cref`.
- `GetStatus` and `ListWorktrees` are synchronous: the port is sync and one
  caller paints a UI badge on the render thread.
- A hung git is killed at 3 seconds and reported as "no workspace", not as an
  error.

## See also

- [`docs/ARCHITECTURE_LAYERS.md`](../../docs/ARCHITECTURE_LAYERS.md) §2 — layer matrix.
- [`docs/adr/ADR-018-harbor-application-split-variant-a.md`](../../docs/adr/ADR-018-harbor-application-split-variant-a.md) — the split decision.
- [`docs/SEAM_GRAPH.md`](../../docs/SEAM_GRAPH.md) — the `IGitQuery` seam.
