using System;
using System.Collections.Generic;
using System.Threading;

namespace Harbor.Tui.CellForge.CodeGen.DevReload;

/// <summary>
///     Issue #2434: atomic dev-reload pipeline over the #2433 interpreter.
///     A poll only records a debounced intent. When the quiet window closes,
///     the whole parse/build runs into an independent candidate; a single
///     <c>Volatile.Write</c> then swaps the live tree.
///     A generation tag discards a candidate that lost a race, and a failed
///     build leaves the last valid snapshot on the screen.
/// </summary>
/// <remarks>
///     Dev-only by placement: this file lives in the Roslyn tooling assembly
///     the CLI references only with <c>HarborWithDslDev=true</c>. The AOT
///     publish switches that flag and the <c>HARBOR_WITH_DSL_DEV</c> symbol
///     off together, so no dev watcher, Roslyn compilation or JIT reaches the
///     native binary. At run time this class only reads text and builds IR.
/// </remarks>
public sealed class DevReloadPipeline
{
    private readonly object _sync = new();
    private readonly IDevReloadCompiler _compiler;
    private readonly IDevReloadFrameGate _frameGate;
    private readonly Func<string> _readSource;
    private readonly TimeSpan _debounce;

    private ReloadSnapshot? _current;
    private int _generation;
    private FileStamp _stamp;
    private string? _pendingSource;
    private DateTimeOffset _pendingSince;
    private bool _hasPending;

    /// <summary>Creates a pipeline over one source reader.</summary>
    /// <param name="readSource">Reads the current full markup text.</param>
    /// <param name="compiler">Candidate builder; defaults to the #2433 interpreter.</param>
    /// <param name="frameGate">Render/layout hook; defaults to an ungated host.</param>
    /// <param name="debounce">Quiet window; defaults to 200 ms.</param>
    public DevReloadPipeline(
        Func<string> readSource,
        IDevReloadCompiler? compiler = null,
        IDevReloadFrameGate? frameGate = null,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(readSource);
        if (debounce is { } window && window < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounce));

        _readSource = readSource;
        _compiler = compiler ?? new DevReloadInterpreterCompiler();
        _frameGate = frameGate ?? new DevReloadFrameGate();
        _debounce = debounce ?? DevReloadTiming.Debounce;
    }

    /// <summary>
    ///     Creates a <see cref="DevFileWatcher"/> that drives this pipeline
    ///     from edits to <paramref name="filePath"/>. The watcher publishes the
    ///     initial tree and, from then on, a fresh tree whenever the file
    ///     changes and the quiet window closes. Callers only dispose the
    ///     watcher to stop; no manual <see cref="Poll"/> calls are needed.
    /// </summary>
    /// <param name="filePath">The watched <c>.harbor-ui</c> or <c>.cs</c> file.</param>
    /// <param name="timeProvider">Clock for ticks and stamps; defaults to the system clock.</param>
    /// <param name="pollInterval">Scan cadence; defaults to <see cref="DevReloadTiming.PollInterval"/>.</param>
    /// <param name="onCommit">Optional observer for each initialization or reload decision.</param>
    public DevFileWatcher WatchFile(
        string filePath,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        Action<ReloadCommitResult>? onCommit = null) =>
        new(filePath, this, timeProvider, pollInterval, onCommit);

    /// <summary>The currently published tree, or null before initialization.</summary>
    public ReloadSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>Generation of the currently published tree.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Builds and publishes the first tree for the given stamp.</summary>
    public ReloadCommitResult Initialize(FileStamp stamp)
    {
        var candidate = _compiler.Compile(_readSource());
        if (!candidate.Success)
            return new ReloadCommitResult(ReloadCommitOutcome.CompileRejected, 0, candidate.Error);

        lock (_sync)
        {
            int generation = ++_generation;
            Volatile.Write(ref _current, new ReloadSnapshot(generation, candidate.Interpreter!));
            _stamp = stamp;
            _hasPending = false;
            _pendingSource = null;
            return new ReloadCommitResult(ReloadCommitOutcome.Published, generation, "initial tree built and published");
        }
    }

    /// <summary>
    ///     Records one stamp observation. Once the source has been quiet for
    ///     the debounce window and no frame owns the tree, a fresh candidate is
    ///     compiled in full and published in one atomic reference write.
    /// </summary>
    /// <param name="stamp">Stamp read by the polling host.</param>
    /// <param name="now">Logical time; tests pass a controlled clock.</param>
    public ReloadCommitResult Poll(FileStamp stamp, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_current is null)
                return new ReloadCommitResult(ReloadCommitOutcome.NotInitialized, 0, "pipeline is not initialized");

            if (!_hasPending && stamp == _stamp)
                return new ReloadCommitResult(ReloadCommitOutcome.Unchanged, _generation, "source stamp unchanged");

            if (stamp != _stamp)
            {
                // A new write inside the window restarts the timer and replaces
                // the source, so a save burst ends in exactly one reload.
                _stamp = stamp;
                _pendingSource = _readSource();
                _pendingSince = now;
                _hasPending = true;
            }
        }

        if (now - _pendingSince < _debounce)
            return new ReloadCommitResult(ReloadCommitOutcome.Debounced, Generation, "debounce window is still open");

        if (_frameGate.IsFrameInProgress)
            return new ReloadCommitResult(ReloadCommitOutcome.FramePhaseRejected, Generation,
                "render/layout owns the tree; reload deferred");

        int expected;
        string source;
        lock (_sync)
        {
            if (!_hasPending)
                return new ReloadCommitResult(ReloadCommitOutcome.Unchanged, _generation, "pending change was superseded");
            expected = _generation;
            source = _pendingSource!;
        }

        // Everything below is off the live tree. A candidate is a complete,
        // independent object: a throw or a rejection cannot half-update a frame.
        var candidate = _compiler.Compile(source);
        if (!candidate.Success)
        {
            lock (_sync)
            {
                if (_generation == expected)
                {
                    _hasPending = false;
                    _pendingSource = null;
                }
            }

            return new ReloadCommitResult(ReloadCommitOutcome.CompileRejected, _generation, candidate.Error);
        }

        var fresh = candidate.Interpreter!;
        var live = Current;
        if (live is not null
            && string.Equals(live.Interpreter.TemplateSignature, fresh.TemplateSignature, StringComparison.Ordinal))
            CopySessions(live.Interpreter, fresh);

        lock (_sync)
        {
            if (_generation != expected)
                return new ReloadCommitResult(ReloadCommitOutcome.StaleGeneration, _generation,
                    "candidate generation " + expected.ToString() + " is stale; last valid tree kept");

            if (_frameGate.IsFrameInProgress)
                return new ReloadCommitResult(ReloadCommitOutcome.FramePhaseRejected, _generation,
                    "render/layout started during compilation; last valid tree kept");

            int generation = ++_generation;
            Volatile.Write(ref _current, new ReloadSnapshot(generation, fresh));
            _hasPending = false;
            _pendingSource = null;
            return new ReloadCommitResult(ReloadCommitOutcome.Published, generation,
                "candidate compiled and published atomically");
        }
    }

    /// <summary>
    ///     Carries host state to the same ids of an unchanged template. A
    ///     rebuilt template keeps no state, matching the #2433 interpreter.
    /// </summary>
    private static void CopySessions(DevReloadInterpreter from, DevReloadInterpreter to)
    {
        foreach (int id in CollectIds(from))
        {
            if (!to.Contains(id))
                continue;
            var state = from.SessionOf(id);
            if (state is not null)
                to.SetSession(id, state);
        }
    }

    private static IEnumerable<int> CollectIds(DevReloadInterpreter interpreter)
    {
        int root = interpreter.LiveRootId;
        if (root == 0)
            yield break;

        var stack = new Stack<int>();
        var seen = new HashSet<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int id = stack.Pop();
            if (!seen.Add(id))
                continue;
            yield return id;
            foreach (int child in interpreter.ChildrenOf(id))
                stack.Push(child);
        }
    }
}
