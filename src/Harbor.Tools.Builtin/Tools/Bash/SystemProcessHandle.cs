using System.Diagnostics;
namespace Harbor.Tools.Builtin;

/// <summary>
///     <see cref="IProcessHandle" /> over a real `System.Diagnostics.Process`
///     (#1590). Each member is a one-liner onto the BCL call it mirrors, so
///     BashTool's orchestration (sentinels, drain, cap) reads identically to
///     the pre-seam code. Kill reuses the shared quiet-kill helper (§ROP-A П.14).
/// </summary>
public sealed class SystemProcessHandle : IProcessHandle
{
    private readonly Process _process;

    /// <inheritdoc />
    public event Action<string?> StdoutLine;

    /// <inheritdoc />
    public event Action<string?> StderrLine;

    /// <summary>Takes ownership of an already-created (but not necessarily started) child.</summary>
    public SystemProcessHandle(Process process)
    {
        _process = process;
    }

    /// <inheritdoc />
    public void BeginReading()
    {
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <inheritdoc />
    public void CancelReading()
    {
        try { _process.CancelOutputRead(); }
        catch (InvalidOperationException) { /* never started reading */ }
        try { _process.CancelErrorRead(); }
        catch (InvalidOperationException) { /* never started reading */ }
    }

    /// <inheritdoc />
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    /// <inheritdoc />
    public bool HasExited => _process.HasExited;

    /// <inheritdoc />
    public int ExitCode => _process.ExitCode;

    /// <inheritdoc />
    public void KillTree() => ToolErrors.KillQuietly(_process);

    /// <inheritdoc />
    public void Dispose() => _process.Dispose();
}
