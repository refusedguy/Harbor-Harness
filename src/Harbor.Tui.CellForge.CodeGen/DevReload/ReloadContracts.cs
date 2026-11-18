using System;
using System.IO;
using System.Threading;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     Default cadence of the dev reload watcher (issue #2434). A 250 ms scan
///     notices writes; a 200 ms quiet window coalesces an editor's save burst
///     into one rebuild.
/// </summary>
public static class DevReloadTiming
{
    /// <summary>How long a source must stay unchanged before a reload starts.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    /// <summary>How often the watched file stamp is scanned.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
}

/// <summary>
///     Lightweight stamp for one watched source file. It is the dev equivalent
///     of gpui-shell's TreeStamp: two cheap aggregates cover both a rewrite
///     and a truncate-to-same-length without reading the file every tick.
/// </summary>
/// <param name="Version">Last UTC write time in ticks; zero when absent.</param>
/// <param name="Length">Length in bytes.</param>
public readonly record struct FileStamp(long Version, long Length)
{
    /// <summary>The stamp of a file that does not exist.</summary>
    public static FileStamp None => new(0, 0);

    /// <summary>Reads mtime and length without opening the file.</summary>
    public static FileStamp FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        return file.Exists ? new FileStamp(file.LastWriteTimeUtc.Ticks, file.Length) : None;
    }
}

/// <summary>
///     A complete candidate tree built away from the live one. The pipeline
///     publishes only after <c>Success</c> is true, so a candidate can
///     never leave a half-updated frame behind.
/// </summary>
/// <param name="Success">Whether the source compiled into a complete tree.</param>
/// <param name="Interpreter">The candidate tree, null on failure.</param>
/// <param name="Error">Parser/interpreter diagnostic, empty on success.</param>
public sealed record ReloadCandidate(bool Success, DevReloadInterpreter? Interpreter, string Error)
{
    /// <summary>Wraps a successfully built tree.</summary>
    public static ReloadCandidate Compiled(DevReloadInterpreter interpreter)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        return new ReloadCandidate(true, interpreter, string.Empty);
    }

    /// <summary>Records a rejected source without a tree.</summary>
    public static ReloadCandidate Failed(string error) => new(false, null, error ?? string.Empty);
}

/// <summary>Builds a candidate tree from markup. No runtime compilation or JIT.</summary>
public interface IDevReloadCompiler
{
    /// <summary>Compiles the full source into an independent tree.</summary>
    ReloadCandidate Compile(string markup);
}

/// <summary>The real compiler: the #2433 interpreter over the one shared parser.</summary>
public sealed class DevReloadInterpreterCompiler : IDevReloadCompiler
{
    /// <inheritdoc />
    public ReloadCandidate Compile(string markup) =>
        DevReloadInterpreter.TryCreate(markup, out var interpreter, out var error)
            ? ReloadCandidate.Compiled(interpreter!)
            : ReloadCandidate.Failed(error);
}

/// <summary>
///     Host hook that tells the pipeline when render or layout owns the live
///     tree. A swap during either phase would corrupt the frame.
/// </summary>
public interface IDevReloadFrameGate
{
    /// <summary>True while a render or layout pass is in progress.</summary>
    bool IsFrameInProgress { get; }
}

/// <summary>
///     Counter-based frame gate. Nested render/layout scopes are supported,
///     and a late Dispose cannot make the counter negative.
/// </summary>
public sealed class DevReloadFrameGate : IDevReloadFrameGate
{
    private int _depth;

    /// <inheritdoc />
    public bool IsFrameInProgress => Volatile.Read(ref _depth) > 0;

    /// <summary>Enters a frame scope; dispose the returned lease to leave it.</summary>
    public IDisposable EnterFrame()
    {
        Interlocked.Increment(ref _depth);
        return new FrameScope(this);
    }

    private sealed class FrameScope : IDisposable
    {
        private readonly DevReloadFrameGate _owner;
        private int _disposed;

        internal FrameScope(DevReloadFrameGate owner) => _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _owner._depth);
        }
    }
}

/// <summary>
///     The one published tree. A pipeline replaces this reference with a
///     single atomic write after the replacement has been fully compiled.
/// </summary>
/// <param name="Generation">Monotonic tag of this version.</param>
/// <param name="Interpreter">The live element tree.</param>
public sealed record ReloadSnapshot(int Generation, DevReloadInterpreter Interpreter)
{
    /// <summary>SHA-256 of the source that produced this tree.</summary>
    public string SourceChecksum => Interpreter.SourceChecksum;
}

/// <summary>Why one reload attempt did or did not publish a tree.</summary>
public enum ReloadCommitOutcome
{
    /// <summary>A fully compiled candidate became the live tree.</summary>
    Published,

    /// <summary>The stamp did not change since the previous scan.</summary>
    Unchanged,

    /// <summary>A change is pending but the debounce window is still open.</summary>
    Debounced,

    /// <summary>Render/layout owns the tree; the attempt was refused and kept for a later poll.</summary>
    FramePhaseRejected,

    /// <summary>The candidate failed to parse; the last valid tree stays live.</summary>
    CompileRejected,

    /// <summary>A newer generation won the race; the stale candidate was discarded.</summary>
    StaleGeneration,

    /// <summary>No initial tree has been published yet.</summary>
    NotInitialized
}

/// <summary>Deterministic result of one reload step.</summary>
/// <param name="Outcome">The decision taken.</param>
/// <param name="Generation">Generation visible after the step.</param>
/// <param name="Reason">Short, human-readable explanation.</param>
public sealed record ReloadCommitResult(ReloadCommitOutcome Outcome, int Generation, string Reason)
{
    /// <summary>True when a new tree became visible to the renderer.</summary>
    public bool DidPublish => Outcome == ReloadCommitOutcome.Published;
}
