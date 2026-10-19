using Harbor.Abstractions.Tools;

namespace Harbor.Application.Sessions;

/// <summary>
///     Discovers the workspace inputs of <see cref="SystemPromptContext" />:
///     project context files (<c>AGENTS.md</c>, <c>CLAUDE.md</c>) from the
///     session's working directory and skill files (<c>*.md</c>) from
///     <c>&lt;workdir&gt;/.harbor/skills</c> plus <c>~/.harbor/skills</c>
///     (ROP-C Z3 — the prompt builder rendered those sections, but the loop
///     hardcoded empty arrays).
/// </summary>
/// <remarks>
///     <para>
///         #1523: this used to be a <c>static class</c> owning a process-wide
///         cache outside DI. The implementation now lives in the injectable
///         <see cref="WorkspaceContextService" /> (registered in the
///         composition root as the <see cref="IWorkspaceContextSource" />
///         singleton), and this type remains as a thin migration forwarder on
///         the same shared instance, so pre-DI call sites keep a single cache.
///         New code injects <see cref="IWorkspaceContextSource" />.
///     </para>
///     <para>
///         All lookups are existence-tolerant: a missing directory or file
///         yields an empty result, never a failure — a workspace without skills
///         is normal, not an error.
///     </para>
/// </remarks>
public static class WorkspaceContextSource
{
    /// <summary>
    ///     The shared workspace source: the very instance the composition root
    ///     registers as <see cref="IWorkspaceContextSource" />, so static and
    ///     DI consumers read one cache (#1523).
    /// </summary>
    public static WorkspaceContextService Instance { get; } = new();

    /// <summary>Project context files probed in the working directory.</summary>
    public static string[] ContextFileNames => WorkspaceContextService.ContextFileNames;

    /// <summary>
    ///     Cached variant of the two per-turn loads. Version = XOR of last-write ticks of
    ///     AGENTS.md/CLAUDE.md and both skill directories. Fast path reuses the cached
    ///     lists without touching skill-file contents.
    /// </summary>
    public static (IReadOnlyList<ContextFile> Files, IReadOnlyList<SkillDescriptor> Skills) GetOrLoadCached(
        string workingDirectory) =>
        Instance.GetOrLoadCached(workingDirectory);

    /// <summary>
    ///     Evict one directory from the workspace cache (tests/benchmarks).
    ///     #1523: the eviction is per instance — tests that must not share the
    ///     cache use a fresh <c>WorkspaceContextService</c> instead.
    /// </summary>
    public static void Invalidate(string workingDirectory) => Instance.Invalidate(workingDirectory);

    /// <summary>Load project context files found in <paramref name="workingDirectory" />.</summary>
    public static IReadOnlyList<ContextFile> LoadContextFiles(string workingDirectory) =>
        Instance.LoadContextFiles(workingDirectory);

    /// <summary>
    ///     Render aggregated MCP server instructions as a prompt block
    ///     (ROP-D Z3). Returns <c>null</c> when no server reported instructions
    ///     so the builder skips the section entirely.
    /// </summary>
    public static string? FormatMcpInstructions(IReadOnlyList<McpServerInstructions>? servers) =>
        Instance.FormatMcpInstructions(servers);

    /// <summary>
    ///     Load available skills from the project-local and global skill
    ///     directories. Project-local entries win on name collisions.
    /// </summary>
    public static IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory) =>
        Instance.LoadSkills(workingDirectory);

    /// <summary>
    ///     Testable core: the global skill directory is injected instead of
    ///     resolved from the user profile.
    /// </summary>
    public static IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory, string? globalSkillsDir) =>
        Instance.LoadSkills(workingDirectory, globalSkillsDir);

    /// <summary>
    ///     Load skills from explicit skills roots (e.g. CLI <c>skill list</c>
    ///     with injected roots). Project root wins on name collisions.
    /// </summary>
    /// <remarks>
    ///     #1240: the returned array is sorted by name (ordinal) — a frozen
    ///     snapshot the per-turn consumer (<c>SystemPromptBuilder</c>) iterates
    ///     as-is instead of paying an <c>OrderBy</c> per turn.
    /// </remarks>
    public static IReadOnlyList<SkillDescriptor> LoadSkillsFromRoots(string? projectSkillsDir, string? globalSkillsDir) =>
        Instance.LoadSkillsFromRoots(projectSkillsDir, globalSkillsDir);
}
