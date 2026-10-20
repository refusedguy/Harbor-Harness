using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;

namespace Harbor.Abstractions.Ide;

/// <summary>
///     Contract for an IDE client — provides the agent core with IDE capabilities:
///     file I/O, workspace navigation, git operations, terminal access, and process execution.
///     Implementations are thin adapters: VS Code extension, JetBrains plugin, or a no-op
///     stub for headless/CLI scenarios. The agent core depends ONLY on this interface.
/// </summary>
/// <remarks>
///     <para>
///         <b>RESERVED, deliberately unimplemented (issue #1831).</b> This contract has NO
///         implementor and NO consumer in the tree today, and that is a DECISION, not an
///         oversight: the IDE bridge is an explicitly deferred product decision
///         (issue #1252, "IDE bridge as product (DEFERRED)") whose body says "Do NOT
///         implement" until the out-of-process plugin story matures. The contract is kept
///         because <c>docs/IDE_INTEGRATION_PATTERNS.md</c> §7.5 names it as the seam a
///         future VS Code / JetBrains adapter would implement, and because it is frozen in
///         <c>PublicAPI.Baseline.txt</c>. Deleting it would take a published promise
///         away while the decision that would replace it is still open.
///     </para>
///     <para>
///         It is pinned by <c>tests/Harbor.Architecture.Tests/DeadPublicInterfaceRule.cs</c>,
///         which allows a zero-implementor public interface ONLY through a named, reasoned
///         reservation row. The row is liveness-checked: the day an adapter lands, the row
///         must go in the same commit.
///     </para>
/// </remarks>
public interface IHarborIdeClient
{
    // ── File I/O ──────────────────────────────────────────────────────────────

    /// <summary>Read the entire contents of a file as UTF-8 text.</summary>
    Task<Result<string>> ReadFileAsync(string path, CancellationToken ct = default);

    /// <summary>Write text to a file, creating parent directories if needed.</summary>
    Task<Result> WriteFileAsync(string path, string content, CancellationToken ct = default);

    /// <summary>Read a specific line range from a file (1-based, inclusive).</summary>
    Task<Result<string>> ReadRangeInFileAsync(string path, int startLine, int endLine, CancellationToken ct = default);

    // ── Workspace navigation ──────────────────────────────────────────────────

    /// <summary>Get all open file paths in the editor.</summary>
    Task<Result<IReadOnlyList<string>>> GetOpenFilesAsync(CancellationToken ct = default);

    /// <summary>Get the currently active file path, if any.</summary>
    Task<Result<Maybe<string>>> GetCurrentFileAsync(CancellationToken ct = default);

    /// <summary>Get the workspace root path (git root or project root).</summary>
    Task<Result<string>> GetWorkspaceRootAsync(CancellationToken ct = default);

    // ── Git operations ────────────────────────────────────────────────────────

    /// <summary>Get the current branch name.</summary>
    Task<Result<string>> GetBranchAsync(CancellationToken ct = default);

    /// <summary>Get the git repository root path.</summary>
    Task<Result<string>> GetGitRootAsync(CancellationToken ct = default);

    /// <summary>Get the diff for the working tree (unstaged + staged).</summary>
    Task<Result<string>> GetDiffAsync(bool includeStaged = true, CancellationToken ct = default);

    // ── Terminal & process execution ──────────────────────────────────────────

    /// <summary>Run a shell command in the workspace and return its output.</summary>
    Task<Result<ProcessResult>> RunCommandAsync(string command, string? workingDir = null, CancellationToken ct = default);

    /// <summary>Start a long-running process (e.g., dev server, test watcher).</summary>
    Task<Result<IProcessHandle>> StartProcessAsync(string command, string? workingDir = null, CancellationToken ct = default);
}

/// <summary>Result of a process execution.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr);

/// <summary>
///     Handle to a running background process.
/// </summary>
/// <remarks>
///     <para>
///         <b>RESERVED, deliberately unimplemented (issue #1831).</b> Part of the same reserved
///         IDE-bridge surface as <see cref="IHarborIdeClient" />: no implementor and no
///         consumer in the tree, pending the deferred product decision in issue #1252. See
///         <c>tests/Harbor.Architecture.Tests/DeadPublicInterfaceRule.cs</c>.
///     </para>
///     <para>
///         NOT the same contract as <c>Harbor.Tools.Builtin.IProcessHandle</c> (the Bash tool's
///         child-process handle), which IS implemented and in use. The two share a simple name
///         and nothing else.
///     </para>
/// </remarks>
public interface IProcessHandle : IAsyncDisposable
{
    /// <summary>Process ID.</summary>
    int ProcessId { get; }

    /// <summary>Wait for the process to exit and get its result.</summary>
    Task<ProcessResult> WaitAsync(CancellationToken ct = default);

    /// <summary>Send a signal to the process (e.g., Ctrl+C).</summary>
    Task<Result> SignalAsync(ProcessSignal signal, CancellationToken ct = default);
}

/// <summary>Signal to send to a process.</summary>
public enum ProcessSignal
{
    /// <summary>Interrupt (Ctrl+C / SIGINT).</summary>
    Interrupt,

    /// <summary>Terminate (SIGTERM).</summary>
    Terminate,

    /// <summary>Force kill (SIGKILL).</summary>
    Kill
}
