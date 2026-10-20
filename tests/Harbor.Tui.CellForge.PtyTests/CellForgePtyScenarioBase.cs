using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Harbor.E2E.Framework;
using Harbor.E2E.Framework.Pty;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Shared lifecycle for CE-5 CellForge PTY scenarios: per-test
///     <see cref="MockLlmServer" />, isolated temp <c>$HOME</c> (passed to the
///     CHILD's environment — the test process env stays untouched), a spawned
///     <c>HARBOR_TUI=consoleex</c> app inside a <see cref="PtySession" />, and
///     an ANSI screen emulation fed incrementally from the raw master stream.
///
///     WHY THERE IS NO <c>[NotInParallel]</c> KEY HERE (#1789)
///
///     The scenarios used to share one <c>"pty"</c> constraint key, which
///     serialized the whole project: 40 classes on one key, so the wall time
///     was the SUM of the durations and the parallelism factor was 1.00. The
///     key was a claim about a shared resource, and there is no such resource:
///     every scenario spawns its OWN pseudo-terminal (its own
///     <c>posix_openpt</c>), its OWN child process, its OWN mock server (a
///     random port) and its OWN temp <c>$HOME</c> (a GUID directory, not a
///     shared path. Nothing in this project is process-wide, so nothing needs
///     a mutex, and the repo's own rule says so
///     (<c>docs/TEST_PATTERNS.md</c> §Parallelism: "use a key when the set of
///     competitors is knowable and enumerable"; here the set is EMPTY).
///
///     The one thing that WAS process-wide was the fixture's
///     <c>Environment.SetEnvironmentVariable("HOME", …)</c>, and the scenarios
///     never needed it: the child receives its <c>HOME</c> explicitly through
///     <see cref="ChildEnv" />. <see cref="PtyScenarioFixture" /> is the
///     env-write-free fixture that replaced it, so the isolation the scenarios
///     rely on is per-test state handed to a child process rather than state
///     the test process mutates. A process-wide write would need the BARE
///     <c>[NotInParallel]</c> form ("completely alone"), which is strictly
///     worse than the key it replaced.
///
///     What bounds the concurrency instead is the
///     <c>[ParallelLimiter&lt;PtyScenarioLimit&gt;]</c> below: the semaphore is
///     keyed by the LIMIT TYPE, so
///     <see cref="PtyScenarioLimit" /> is one two-permit gate for the whole
///     assembly. Each scenario spawns a .NET child, so the resource being
///     bounded is CPU, not the terminal, and two children at a time is what a
///     four-core runner carries without squeezing the scenarios that measure
///     the app's own clock.
/// </summary>
[ParallelLimiter<PtyScenarioLimit>]
public abstract class CellForgePtyScenarioBase
{
    private const string CliProjectRelativePath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";

    /// <summary>Default bound for a screen condition wait.</summary>
    private const int DefaultScreenTimeoutMs = 10_000;

    /// <summary>
    ///     Upper bound on how long the pump sleeps between "the child produced
    ///     nothing new" checks. The pump is woken by the output signal long
    ///     before this cap matters; the cap only bounds how quickly a dead
    ///     child is noticed.
    /// </summary>
    private static readonly TimeSpan PumpIdleCap = TimeSpan.FromMilliseconds(100);

    private readonly object _screenLock = new();
    private AnsiTerminalBuffer _screen = new(100, 30);
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private int _consumedRaw;
    private CancellationTokenSource? _pumpCts;

    // ── Repaint signal ──────────────────────────────────────────────────────
    // The emulated grid has exactly ONE writer (the pump thread). Every
    // repaint bumps a version counter and releases the waiters, so a test can
    // wait for "the grid changed" instead of sleeping a fixed interval and
    // hoping. The signal is raised by the very code that writes the grid, so
    // a waiter can never miss a change that already landed.
    private long _screenVersion;
    private TaskCompletionSource _screenRepainted = NewSignal();

    /// <summary><see cref="Environment.TickCount64" /> of the last repaint.</summary>
    private long _lastRepaintTick;

    /// <summary>
    ///     The per-test mock server + temp <c>$HOME</c>. <see cref="PtyScenarioFixture" />
    ///     and not <c>MockServerFixture</c>: the latter also swaps the PROCESS
    ///     <c>HOME</c>/<c>USERPROFILE</c>, which is the one thing that would
    ///     force these scenarios to be serialized against each other (#1789).
    /// </summary>
    [ClassDataSource<PtyScenarioFixture>(Shared = SharedType.None)]
    public required PtyScenarioFixture Fixture { get; init; }

    protected MockLlmServer Server => Fixture.Server;

    protected string TempHome => Fixture.TempHome;

    protected PtySession Session { get; private set; } = null!;

    protected int Cols { get; private set; } = 100;

    protected int Rows { get; private set; } = 30;

    /// <summary>Probe escape hatch: Information-level app logging for diagnostics.</summary>
    protected bool VerboseLogging { get; set; }

    [Before(Test)]
    public async Task SetUpScenarioAsync()
    {
        PtySession.RequireUnix();
    }

    [After(Test)]
    public async Task TearDownScenarioAsync()
    {
        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
        }

        if (Session is not null)
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Spawn Harbor.App.Cli (interactive mode) inside a fresh PTY.</summary>
    /// <param name="tui">Backend id for the child's <c>HARBOR_TUI</c> — the canonical
    /// <c>cellforge</c> or its legacy alias <c>consoleex</c> (both must render identically).</param>
    protected async Task StartAppAsync(int cols = 100, int rows = 30, string tui = "consoleex")
    {
        // Relaunch support (alias-equivalence scenarios boot twice in one test):
        // reap the previous child and its pump before replacing them.
        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        if (Session is not null)
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            Session = null!;
        }

        Cols = cols;
        Rows = rows;
        lock (_screenLock)
        {
            _screen = new AnsiTerminalBuffer(cols, rows);
            _decoder = Encoding.UTF8.GetDecoder();
            _consumedRaw = 0;
        }

        // A fresh grid is a fresh timeline for the repaint signal too.
        Interlocked.Exchange(ref _screenRepainted, NewSignal());
        Interlocked.Increment(ref _screenVersion);
        Volatile.Write(ref _lastRepaintTick, Environment.TickCount64);

        string dll = ResolveCliDllPath();
        var spec = new PtyStartSpec(
            FileName: ResolveDotnetHost(),
            Args: ["exec", dll],
            Cols: cols,
            Rows: rows,
            Environment: ChildEnv(tui));
        Session = PtySession.Start(spec);

        _pumpCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpLoopAsync(_pumpCts.Token));
    }

    /// <summary>Current visible grid as raw text (emulated terminal state).</summary>
    protected string ScreenText
    {
        get
        {
            lock (_screenLock)
            {
                return _screen.GetVisibleText();
            }
        }
    }

    /// <summary>Visible grid normalized: trailing whitespace trimmed per line.</summary>
    protected string[] NormalizedLines()
    {
        return NormalizeLines(ScreenText);
    }

    /// <summary>Normalization contract: TrimEnd each line, drop trailing empty lines.</summary>
    internal static string[] NormalizeLines(string visibleText)
    {
        var lines = visibleText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd();
        }

        int last = lines.Length - 1;
        while (last >= 0 && lines[last].Length == 0)
        {
            last--;
        }

        return lines[..(last + 1)];
    }

    /// <summary>Normalized grid as single text — golden comparison form.</summary>
    internal static string NormalizeToGoldenText(string visibleText) =>
        string.Join("\n", NormalizeLines(visibleText));

    /// <summary>
    /// R2 steal: scrollback rows scrolled off the top (oldest first) — the
    /// buffer that makes asserts on virtualized ленты possible.
    /// </summary>
    protected string[] ScrollbackLines()
    {
        lock (_screenLock)
        {
            return _screen.GetScrollbackLines();
        }
    }

    /// <summary>
    /// R2 steal: per-line assert over the live grid — the PTY-side half of the
    /// single idiom shared with <c>TestBackend.AssertBufferLinesAsync</c>.
    /// </summary>
    protected Task AssertScreenLinesAsync(params string[] expected) =>
        PtyBufferAsserts.AssertBufferLinesAsync(NormalizedLines(), expected);

    /// <summary>
    /// R2 steal: per-line assert over the scrollback — pairs with
    /// <c>TestBackend.AssertScrollbackLinesAsync</c>.
    /// </summary>
    protected Task AssertScrollbackLinesAsync(params string[] expected) =>
        PtyBufferAsserts.AssertScrollbackLinesAsync(ScrollbackLines(), expected);

    /// <summary>
    ///     Wait until <paramref name="predicate" /> holds over the emulated
    ///     grid, then return the grid it matched on.
    ///     <para />
    ///     Condition wait, not a sleep: the waiter is woken by the repaint
    ///     signal the pump raises, so the wait costs exactly the child's
    ///     latency (no poll interval of overshoot) and it can never miss a
    ///     repaint that already landed. On timeout the failure says WHICH
    ///     condition was not met — the caller's own predicate expression is
    ///     captured by the compiler and quoted back — instead of only how
    ///     long we waited.
    /// </summary>
    /// <param name="predicate">Condition over the normalized visible lines.</param>
    /// <param name="timeout">Bound on the whole wait. Defaults to 10 s.</param>
    /// <param name="predicateSource">
    ///     The caller's predicate expression, captured by the compiler. It is
    ///     what the timeout diagnostic quotes as "expected".
    /// </param>
    protected async Task<string[]> WaitForScreenAsync(
        Func<string[], bool> predicate,
        TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(predicate))] string? predicateSource = null)
    {
        TimeSpan deadline = timeout ?? TimeSpan.FromMilliseconds(DefaultScreenTimeoutMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            // Read the version BEFORE the grid: a repaint that lands between
            // the two is then seen by the wait below instead of being missed.
            long version = ScreenVersion;
            string[] lines = NormalizedLines();
            if (predicate(lines))
            {
                return lines;
            }

            TimeSpan remaining = deadline - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            bool repainted = await WaitForRepaintAsync(version, remaining).ConfigureAwait(false);
            if (!repainted)
            {
                break;
            }
        }

        throw new TimeoutException(DescribeScreenTimeout(deadline, sw.Elapsed, predicateSource));
    }

    protected Task<bool> WaitForRawTextAsync(string needle, TimeSpan? timeout = null) =>
        Session.WaitForTextAsync(needle, timeout);

    /// <summary>
    /// PTY-side idle wait (#1183 Pilot steal — the out-of-process analogue of
    /// the in-process <c>WaitForIdle</c>): resolves when the emulated grid
    /// stops changing for <paramref name="stableFor" /> instead of sleeping a
    /// fixed delay. A fixed <c>Task.Delay(n)</c> always pays <c>n</c> and still
    /// flakes on slow CI; this pays only what the child needed and never
    /// proceeds mid-repaint. Never throws: on timeout it returns the current
    /// grid so the caller's <c>WaitForScreenAsync</c> still guards.
    /// <para />
    /// The stability window is measured against the REAL repaint times (the
    /// pump stamps every repaint) and the child must have no un-pumped bytes
    /// left, so "settled" means exactly that — nothing has changed the grid
    /// for <paramref name="stableFor" />. The old implementation re-read the
    /// grid on a fixed 100 ms tick and always paid the full window plus a
    /// tick of overshoot.
    /// <para />
    /// Keep fixed delays where the timing itself is the subject (Ctrl+C
    /// gesture window, PTY chunk gaps) — quiescence is for "let the UI
    /// settle" pauses only.
    /// </summary>
    protected async Task<string[]> WaitForQuiescenceAsync(TimeSpan? stableFor = null, TimeSpan? timeout = null)
    {
        TimeSpan stable = stableFor ?? TimeSpan.FromMilliseconds(300);
        TimeSpan deadline = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            long quietMs = Environment.TickCount64 - Volatile.Read(ref _lastRepaintTick);
            bool childDrained = PendingRawBytes == 0;
            if (childDrained && quietMs >= stable.TotalMilliseconds)
            {
                break;
            }

            TimeSpan remaining = deadline - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // Wake on the next repaint. The cap keeps the loop honest when the
            // grid is quiet but the child still has bytes in flight.
            TimeSpan wait = remaining < PumpIdleCap ? remaining : PumpIdleCap;
            _ = await WaitForRepaintAsync(wait).ConfigureAwait(false);
        }

        return NormalizedLines();
    }

    /// <summary>
    /// Answer the child's approval gate and keep answering it until the
    /// turn's follow-up marker lands.
    /// <para />
    /// This replaces the "poll every N ms, send 'a' while the prompt is
    /// visible" loop the error scenarios used. The prompt and the follow-up
    /// text are OBSERVED states: the loop wakes on the repaint signal, so the
    /// 'a' goes out the moment the gate is painted instead of up to a full
    /// poll interval later — and the gate is re-answered while it stays on
    /// screen, because the child may drop a keypress and a lost key would
    /// hang the turn.
    /// </summary>
    /// <param name="promptMarker">Text the approval gate paints.</param>
    /// <param name="doneMarker">Text that proves the turn moved on.</param>
    /// <param name="timeout">Bound on the whole wait.</param>
    /// <param name="answerKey">Key that answers the gate ("a" allows, "n" denies).</param>
    /// <param name="onFirstAnswer">
    /// Invoked once, the first time the gate is answered — the hook the
    /// scenarios use to swap the mock's response.
    /// </param>
    /// <returns><see langword="true" /> when the gate was answered at least once.</returns>
    protected async Task<bool> AnswerApprovalGateAsync(
        string promptMarker,
        string doneMarker,
        TimeSpan timeout,
        string answerKey = "a",
        Action? onFirstAnswer = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool approved = false;
        long lastAnswerTick = 0;
        while (sw.Elapsed < timeout)
        {
            string[] snap = NormalizedLines();
            if (snap.Any(x => x.Contains(promptMarker, StringComparison.Ordinal)))
            {
                // Re-answer at most every 250 ms — the SAME rate the old
                // 250 ms poll loop used. The gate stays painted for as long
                // as the tool runs, and a repaint-driven loop must not turn
                // into a key flood (an extra 'a' would land in the composer).
                long now = Environment.TickCount64;
                if (lastAnswerTick == 0 || now - lastAnswerTick >= 250)
                {
                    Session.SendKey(answerKey);
                    lastAnswerTick = now;
                    if (!approved)
                    {
                        approved = true;
                        onFirstAnswer?.Invoke();
                    }
                }
            }

            if (snap.Any(x => x.Contains(doneMarker, StringComparison.Ordinal)))
            {
                break;
            }

            TimeSpan remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            // Wake on the next repaint — that is the signal that says "the
            // gate appeared / the turn moved on, look again".
            TimeSpan wait = remaining < PumpIdleCap ? remaining : PumpIdleCap;
            _ = await WaitForRepaintAsync(wait).ConfigureAwait(false);
        }

        return approved;
    }

    /// <summary>
    /// Wait until the emulated grid is repainted, or the timeout elapses.
    /// Returns <see langword="true" /> when a repaint was observed.
    /// </summary>
    protected Task<bool> WaitForRepaintAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitForRepaintAsync(ScreenVersion, timeout, cancellationToken);

    private async Task<bool> WaitForRepaintAsync(
        long observedVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var signal = Volatile.Read(ref _screenRepainted);
            if (ScreenVersion != observedVersion)
            {
                return true;
            }

            TimeSpan remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            var expiry = Task.Delay(remaining, cancellationToken);
            if (await Task.WhenAny(signal.Task, expiry).ConfigureAwait(false) != signal.Task)
            {
                return false;
            }
        }
    }

    /// <summary>Monotonic version of the emulated grid; bumped on every repaint.</summary>
    private long ScreenVersion => Volatile.Read(ref _screenVersion);

    /// <summary>Bytes the child produced that the pump has not written to the grid yet.</summary>
    private int PendingRawBytes
    {
        get
        {
            lock (_screenLock)
            {
                return Math.Max(0, Session.OutputLength - _consumedRaw);
            }
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalRepaint()
    {
        var previous = Interlocked.Exchange(ref _screenRepainted, NewSignal());
        previous.TrySetResult();
    }

    /// <summary>
    /// The timeout message: what was expected (the caller's predicate), what
    /// was seen (the grid), and the raw tail the child produced.
    /// </summary>
    private string DescribeScreenTimeout(TimeSpan deadline, TimeSpan waited, string? predicateSource)
    {
        var sb = new StringBuilder();
        sb.Append("screen condition not met within ")
            .Append(deadline)
            .Append(" (waited ")
            .Append(waited.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture))
            .AppendLine("s).");
        sb.Append("expected: ").AppendLine(predicateSource ?? "<predicate>");
        sb.AppendLine("Raw tail:");
        sb.AppendLine(Tail(Session.RawText, 4000));
        sb.AppendLine("Screen:");
        sb.Append(ScreenText);
        return sb.ToString();
    }

    /// <summary>Type text + Enter into the composer (raw byte '\r' = Enter in raw mode).</summary>
    protected void SubmitLine(string text) => Session.WriteLine(text);

    /// <summary>Send Ctrl+C (byte 0x03 — ISIG is off in raw mode).</summary>
    protected void SendCtrlC() => Session.SendKey("\x03");

    /// <summary>Overwrite the child's <c>$HOME/.harbor/config.json</c> from the test side.
    /// Call before <see cref="StartAppAsync" /> (pre-launch seeding) or mid-session
    /// (runtime-toggle survival); the fixture's installer wrote the baseline.</summary>
    protected void WriteHomeConfig(string json) =>
        File.WriteAllText(Path.Combine(TempHome, ".harbor", "config.json"), json);

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        // Decoder state is pump-private; only screen writes take the lock.
        var chunkBuf = new char[8192];
        bool drainedAfterExit = false;
        while (!ct.IsCancellationRequested)
        {
            byte[] delta;
            lock (_screenLock)
            {
                delta = Session.RawOutputFrom(_consumedRaw);
            }

            if (delta.Length == 0)
            {
                if (Session.HasExited && drainedAfterExit)
                {
                    return;
                }

                drainedAfterExit = Session.HasExited;

                // Sleep on the child's NEXT BYTE instead of on a fixed tick.
                // The old Thread.Sleep(20) added up to 20 ms of latency to
                // every repaint (the child had already produced the bytes —
                // the pump just had not looked yet) and burned a core
                // spinning at 50 wake-ups a second for the whole test.
                _ = await Session
                    .WaitForOutputChangeAsync(Session.OutputLength, PumpIdleCap, ct)
                    .ConfigureAwait(false);
                continue;
            }

            drainedAfterExit = false;
            int chars = _decoder.GetChars(delta, 0, delta.Length, chunkBuf, 0);
            if (chars <= 0)
            {
                lock (_screenLock)
                {
                    _consumedRaw += delta.Length;
                }

                continue;
            }

            string chunk = new(chunkBuf, 0, chars);
            lock (_screenLock)
            {
                _screen.Write(chunk);

                // The bytes count as consumed only AFTER the grid shows them.
                // Counting them earlier would let a quiescence check see "no
                // pending bytes" while the repaint is still owed.
                _consumedRaw += delta.Length;
                Volatile.Write(ref _lastRepaintTick, Environment.TickCount64);
                Interlocked.Increment(ref _screenVersion);
            }

            // Release the screen waiters AFTER the grid is written: a waiter
            // that wakes on this signal must see the new grid.
            SignalRepaint();
        }
    }

    private static string Tail(string text, int max)
    {
        return text.Length <= max ? text : text[^max..];
    }

    private Dictionary<string, string> ChildEnv(string tui = "consoleex") => new()
    {
        ["HOME"] = TempHome,
        ["USERPROFILE"] = TempHome,
        ["HARBOR_MODEL"] = "mock/test-model",
        ["MOCK_API_KEY"] = "pty-test-key",
        ["HARBOR_LOGLEVEL"] = VerboseLogging ? "Information" : "Warning",
        ["HARBOR_SKIP_ONBOARDING"] = "1",
        ["HARBOR_TUI"] = tui,
        ["HARBOR_MASCOT"] = "off", // ambient cat blinks per tick — byte-exact goldens need it out of the frame
        // Background AI title upgrade races wire-order assertions by design
        // (submit flows assert requests[^1]); auto-title has no PTY coverage.
        ["HARBOR_NO_AUTOTITLE"] = "1",
        ["TERM"] = "xterm-256color",
        ["LANG"] = "C.UTF-8",
        ["LC_ALL"] = "C.UTF-8",
    };

    private static string ResolveCliDllPath()
    {
        string projectPath = HarborAppLocator.ResolveProjectPath(CliProjectRelativePath);
        string projectDir = Path.GetDirectoryName(projectPath)!;
        string release = Path.Combine(projectDir, "bin", "Release", "net10.0", "Harbor.App.Cli.dll");
        string debug = Path.Combine(projectDir, "bin", "Debug", "net10.0", "Harbor.App.Cli.dll");

        // Release first, and Debug only when it is the only thing there or
        // when it is asked for by name. The old order preferred Debug, so a
        // developer with a stale Debug build ran the scenarios against a
        // binary that no longer matched the source and read the failures as
        // product bugs (#1789). CI has only Release, so this changes nothing
        // there; locally it makes the binary the test runs explicit.
        if (File.Exists(release) &&
            !string.Equals(
                Environment.GetEnvironmentVariable("HARBOR_TEST_CLI_CONFIG"),
                "Debug",
                StringComparison.OrdinalIgnoreCase))
        {
            return release;
        }

        if (File.Exists(debug))
        {
            return debug;
        }

        if (File.Exists(release))
        {
            return release;
        }

        throw new FileNotFoundException(
            "Harbor.App.Cli.dll not built — run dotnet build before PTY scenarios.", release);
    }

    /// <summary>
    ///     The muxer that spawns the child.
    ///     <para />
    ///     The test host IS a <c>dotnet</c> process (CI runs
    ///     <c>dotnet exec &lt;testdll&gt;</c>), so its own executable is the one
    ///     muxer that is guaranteed to be able to run the child, and it is the
    ///     same one the runner already uses. This matters because the fallback
    ///     (<see cref="HarborAppLocator.ResolveDotnetHost" />) resolves
    ///     <c>$HOME/.dotnet/dotnet</c>, and the scenarios no longer control the
    ///     process <c>HOME</c> (#1789): a developer with an older SDK installed
    ///     under <c>~/.dotnet</c> would have had the child spawned by it, and an
    ///     older muxer cannot run a <c>net10.0</c> app.
    /// </summary>
    private static string ResolveDotnetHost()
    {
        string? processPath = Environment.ProcessPath;
        if (processPath is not null
            && string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(processPath))
        {
            return processPath;
        }

        return HarborAppLocator.ResolveDotnetHost();
    }
}
