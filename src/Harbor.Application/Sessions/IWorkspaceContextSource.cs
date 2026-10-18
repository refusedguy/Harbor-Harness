using Harbor.Abstractions.Tools;

namespace Harbor.Application.Sessions;

/// <summary>
///     Workspace inputs of <see cref="SystemPromptContext" />: project context
///     files (<c>AGENTS.md</c>, <c>CLAUDE.md</c>), skill discovery for the
///     per-turn prompt and MCP instruction rendering (ROP-D Z3).
/// </summary>
/// <remarks>
///     <para>
///         #1523: the workspace snapshot cache is instance state on the
///         implementation, not a static global. The composition root registers
///         this interface as a singleton (the shared instance the static
///         <c>WorkspaceContextSource</c> migration facade forwards to), and
///         tests own fresh instances instead of hand-evicting a static cache.
///     </para>
///     <para>
///         All lookups are existence-tolerant: a missing directory or file
///         yields an empty result, never a failure — a workspace without
///         skills is normal, not an error.
///     </para>
/// </remarks>
public interface IWorkspaceContextSource
{
    /// <summary>
    ///     Cached variant of the two per-turn loads. Version = XOR of last-write ticks of
    ///     AGENTS.md/CLAUDE.md and both skill directories. Fast path reuses the cached
    ///     lists without touching skill-file contents.
    /// </summary>
    (IReadOnlyList<ContextFile> Files, IReadOnlyList<SkillDescriptor> Skills) GetOrLoadCached(
        string workingDirectory);

    /// <summary>Evict one directory from this instance's workspace cache (tests/benchmarks).</summary>
    void Invalidate(string workingDirectory);

    /// <summary>Load project context files found in <paramref name="workingDirectory" />.</summary>
    IReadOnlyList<ContextFile> LoadContextFiles(string workingDirectory);

    /// <summary>
    ///     Render aggregated MCP server instructions as a prompt block
    ///     (ROP-D Z3). Returns <c>null</c> when no server reported instructions
    ///     so the builder skips the section entirely.
    /// </summary>
    string? FormatMcpInstructions(IReadOnlyList<McpServerInstructions>? servers);

    /// <summary>
    ///     Load available skills from the project-local and global skill
    ///     directories. Project-local entries win on name collisions.
    /// </summary>
    IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory);

    /// <summary>
    ///     Testable core: the global skill directory is injected instead of
    ///     resolved from the user profile.
    /// </summary>
    IReadOnlyList<SkillDescriptor> LoadSkills(string workingDirectory, string? globalSkillsDir);

    /// <summary>
    ///     Load skills from explicit skills roots (e.g. CLI <c>skill list</c>
    ///     with injected roots). Project root wins on name collisions; the
    ///     returned snapshot is pre-sorted, ordinal by name (#1240).
    /// </summary>
    IReadOnlyList<SkillDescriptor> LoadSkillsFromRoots(string? projectSkillsDir, string? globalSkillsDir);
}
