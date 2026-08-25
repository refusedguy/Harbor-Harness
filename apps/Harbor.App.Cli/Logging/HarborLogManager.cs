using System.Diagnostics;
using Microsoft.Extensions.Logging;
namespace Harbor.App.Cli.Logging;
/// <summary>
///     Process-wide owner of the shared <see cref="FileLoggerProvider" />.
///     Both <c>Program.Main</c> (which builds a tiny logger factory for
///     one-shot commands like <c>harbor --version</c>) and
///     <c>HostBuilder.Build</c> (which builds the full host) call
///     <see cref="Initialize" /> to obtain the same singleton provider, so
///     every log line — from the very first line of <c>Main</c> to the last
///     line emitted during host disposal — lands in the same per-run file.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a singleton:</b> the previous setup had two independent
///         logger factories — one in Program.cs, one in HostBuilder. The
///         FileLogger was only attached to Program.cs's factory, so all of
///         the host-build phase logging (RegisterCore, RegisterRegistries,
///         plugin loading, …) went nowhere. The user could not "run the app
///         and see ALL logs from start to end".
///     </para>
///     <para>
///         <b>Console level vs file level:</b> the file always captures down
///         to <see cref="LogLevel.Debug" /> (overridable via
///         <see cref="Initialize(string, LogLevel?)" />'s <c>fileLevel</c>
///         argument) for post-mortem analysis. The console level is decided
///         by the caller — typically <see cref="LogLevel.Debug" /> when a
///         debugger is attached, <see cref="LogLevel.Information" /> otherwise.
///     </para>
///     <para>
///         <b>Lifetime:</b> the provider is disposed by whichever logger
///         factory is disposed first (the host's, when the
///         <c>using var host = …</c> exits). <see cref="FileLoggerProvider.Dispose" />
///         is idempotent, so subsequent disposes are no-ops. Tests that build
///         the host multiple times in one process get separate per-run files
///         — one per <see cref="Initialize" /> call — because each call closes
///         the previous provider before opening a new one.
///     </para>
/// </remarks>
public static class HarborLogManager
{
    private static readonly object InitLock = new();
    private static bool _cleanupDone;

    /// <summary>
    ///     The shared provider, or <see langword="null" /> if
    ///     <see cref="Initialize" /> has not been called yet.
    /// </summary>
    public static FileLoggerProvider? Current
    {
        get;
        private set;
    }

    /// <summary>
    ///     Absolute path to the directory under which per-run log files are
    ///     written (<c>~/.harbor/logs/</c>). Constant — exposed so callers
    ///     don't have to re-derive it.
    /// </summary>
    public static string DefaultLogDirectory
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".harbor", "logs");
        }
    }

    /// <summary>
    ///     Create (or reuse) the shared <see cref="FileLoggerProvider" />.
    ///     Subsequent calls return the already-initialized instance until
    ///     <see cref="Shutdown" /> is called.
    /// </summary>
    /// <param name="appPrefix">Short prefix embedded in the filename (e.g. "cli").</param>
    /// <param name="fileLevel">
    ///     Minimum level captured to the file. Defaults to
    ///     <see cref="LogLevel.Debug" /> so post-mortem always has full detail.
    /// </param>
    /// <returns>The shared provider. Never <see langword="null" />.</returns>
    public static FileLoggerProvider Initialize(string appPrefix, LogLevel? fileLevel = null)
    {
        lock (InitLock)
        {
            if (Current is not null)
                return Current;

            string logDir = DefaultLogDirectory;
            var provider = new FileLoggerProvider(
                appPrefix,
                logDir,
                fileLevel ?? LogLevel.Debug);

            if (!_cleanupDone)
            {
                try
                {
                    new RollingLogCleaner(logDir, 50).Cleanup();
                }
                catch
                {
                    // Cleanup is best-effort — never block startup on log housekeeping.
                }
                _cleanupDone = true;
            }

            Current = provider;
            return provider;
        }
    }

    /// <summary>
    ///     Dispose the current provider and reset state so the next
    ///     <see cref="Initialize" /> call opens a fresh file. Intended for
    ///     tests that need a clean per-test log; production code never calls
    ///     this — the OS reclaims the file handle on process exit.
    /// </summary>
    public static void Shutdown()
    {
        lock (InitLock)
        {
            Current?.Dispose();
            Current = null;
        }
    }

    /// <summary>
    ///     Resolve the console log level from CLI args / env, with a sensible
    ///     default: <see cref="LogLevel.Debug" /> when a debugger is attached,
    ///     <see cref="LogLevel.Information" /> otherwise. The previous default
    ///     was <see cref="LogLevel.Warning" />, which is why the user "only saw
    ///     a minimal log".
    /// </summary>
    /// <param name="args">CLI args (parsed for <c>--loglevel</c>/<c>-ll</c>).</param>
    public static LogLevel ResolveConsoleLevel(string[] args)
    {
        string? raw = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--loglevel", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--log-level", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-ll", StringComparison.OrdinalIgnoreCase))
            {
                raw = args[i + 1];
                break;
            }
        }
        // Also accept --loglevel=Info form.
        if (raw is null)
        {
            foreach (string a in args)
            {
                if (a.StartsWith("--loglevel=", StringComparison.OrdinalIgnoreCase))
                    raw = a["--loglevel=".Length..];
                else if (a.StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
                    raw = a["--log-level=".Length..];
                if (raw is not null) break;
            }
        }
        raw ??= Environment.GetEnvironmentVariable("HARBOR_LOGLEVEL");
        if (Enum.TryParse(raw, true, out LogLevel parsed))
            return parsed;
        return Debugger.IsAttached ? LogLevel.Debug : LogLevel.Information;
    }
}
