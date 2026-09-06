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
///     All scenario classes share the "pty" NotInParallel constraint key:
///     process spawn + real PTYs are serialized by design.
/// </summary>
[NotInParallel("pty")]
[ParallelLimiter<MockServerLimit>]
public abstract class CellForgePtyScenarioBase
{
    private const string CliProjectRelativePath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";

    private readonly object _screenLock = new();
    private AnsiTerminalBuffer _screen = new(100, 30);
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private int _consumedRaw;
    private CancellationTokenSource? _pumpCts;

    [ClassDataSource<MockServerFixture>(Shared = SharedType.None)]
    public required MockServerFixture Fixture { get; init; }

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
    protected Task StartAppAsync(int cols = 100, int rows = 30)
    {
        Cols = cols;
        Rows = rows;
        lock (_screenLock)
        {
            _screen = new AnsiTerminalBuffer(cols, rows);
            _decoder = Encoding.UTF8.GetDecoder();
            _consumedRaw = 0;
        }

        string dll = ResolveCliDllPath();
        var spec = new PtyStartSpec(
            FileName: HarborAppLocator.ResolveDotnetHost(),
            Args: ["exec", dll],
            Cols: cols,
            Rows: rows,
            Environment: ChildEnv());
        Session = PtySession.Start(spec);

        _pumpCts = new CancellationTokenSource();
        _ = Task.Run(() => PumpLoop(_pumpCts.Token));
        return Task.CompletedTask;
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

    protected async Task<string[]> WaitForScreenAsync(Func<string[], bool> predicate, TimeSpan? timeout = null)
    {
        var deadline = TimeSpan.FromSeconds(10);
        if (timeout is { } t)
        {
            deadline = t;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < deadline)
        {
            string[] lines = NormalizedLines();
            if (predicate(lines))
            {
                return lines;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"screen condition not met within {deadline}. Raw tail:\n{Tail(Session.RawText, 4000)}\nScreen:\n{ScreenText}");
    }

    protected Task<bool> WaitForRawTextAsync(string needle, TimeSpan? timeout = null) =>
        Session.WaitForTextAsync(needle, timeout);

    /// <summary>Type text + Enter into the composer (raw byte '\r' = Enter in raw mode).</summary>
    protected void SubmitLine(string text) => Session.WriteLine(text);

    /// <summary>Send Ctrl+C (byte 0x03 — ISIG is off in raw mode).</summary>
    protected void SendCtrlC() => Session.SendKey("\x03");

    private void PumpLoop(CancellationToken ct)
    {
        // Decoder state is pump-thread-private; only screen writes take the lock.
        var chunkBuf = new char[8192];
        bool drainedAfterExit = false;
        while (!ct.IsCancellationRequested)
        {
            byte[] delta;
            lock (_screenLock)
            {
                delta = Session.RawOutputFrom(_consumedRaw);
                _consumedRaw += delta.Length;
            }

            if (delta.Length == 0)
            {
                if (Session.HasExited && drainedAfterExit)
                {
                    return;
                }

                drainedAfterExit = Session.HasExited;
                Thread.Sleep(20);
                continue;
            }

            drainedAfterExit = false;
            int chars = _decoder.GetChars(delta, 0, delta.Length, chunkBuf, 0);
            if (chars <= 0)
            {
                continue;
            }

            string chunk = new(chunkBuf, 0, chars);
            lock (_screenLock)
            {
                _screen.Write(chunk);
            }
        }
    }

    private static string Tail(string text, int max)
    {
        return text.Length <= max ? text : text[^max..];
    }

    private Dictionary<string, string> ChildEnv() => new()
    {
        ["HOME"] = TempHome,
        ["USERPROFILE"] = TempHome,
        ["HARBOR_MODEL"] = "mock/test-model",
        ["MOCK_API_KEY"] = "pty-test-key",
        ["HARBOR_LOGLEVEL"] = VerboseLogging ? "Information" : "Warning",
        ["HARBOR_SKIP_ONBOARDING"] = "1",
        ["HARBOR_TUI"] = "consoleex",
        ["HARBOR_MASCOT"] = "off", // ambient cat blinks per tick — byte-exact goldens need it out of the frame
        ["TERM"] = "xterm-256color",
        ["LANG"] = "C.UTF-8",
        ["LC_ALL"] = "C.UTF-8",
    };

    private static string ResolveCliDllPath()
    {
        string projectPath = HarborAppLocator.ResolveProjectPath(CliProjectRelativePath);
        string projectDir = Path.GetDirectoryName(projectPath)!;
        string debug = Path.Combine(projectDir, "bin", "Debug", "net10.0", "Harbor.App.Cli.dll");
        if (File.Exists(debug))
        {
            return debug;
        }

        string release = Path.Combine(projectDir, "bin", "Release", "net10.0", "Harbor.App.Cli.dll");
        if (File.Exists(release))
        {
            return release;
        }

        throw new FileNotFoundException(
            "Harbor.App.Cli.dll not built — run dotnet build before PTY scenarios.", debug);
    }
}
