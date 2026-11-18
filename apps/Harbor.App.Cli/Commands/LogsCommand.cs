using CSharpFunctionalExtensions;
using Harbor.App.Cli.Logging;
using System.Globalization;
using System.Threading.Tasks;
namespace Harbor.App.Cli.Commands;
/// <summary>
///     <c>harbor logs</c> — inspect per-run log files written by
///     <see cref="FileLoggerProvider" /> to <c>~/.harbor/logs/</c>.
/// </summary>
/// <remarks>
///     <para>
///         Subcommands:
///     </para>
///     <list type="table">
///         <item>
///             <term>(default)</term><description>List the 10 most recent log files (same as <c>--list</c>).</description>
///         </item>
///         <item>
///             <term>--list</term><description>List all log files, newest first.</description>
///         </item>
///         <item>
///             <term>--last</term><description>Print the most recent log file to stdout.</description>
///         </item>
///         <item>
///             <term>--follow</term>
///             <description>Print the most recent log file, then tail new lines (Ctrl-C to exit).</description>
///         </item>
///         <item>
///             <term>--clean</term>
///             <description>Delete every <c>harbor-*.log</c> file (after confirmation prompt).</description>
///         </item>
///         <item>
///             <term>--spans [--slow MS] [--top N]</term>
///             <description>Show the slowest finished spans from the telemetry export.</description>
///         </item>
///         <item>
///             <term>--help</term><description>Show usage.</description>
///         </item>
///     </list>
///     <para>
///         <see cref="FileLoggerProvider" /> opens files with
///         <see cref="FileShare.Read" />, so <c>--follow</c> and external
///         <c>tail -f</c> can read a file the CLI is actively writing to.
///     </para>
/// </remarks>
public sealed class LogsCommand : ICommand
{
    private static readonly string LogDir = HarborLogManager.DefaultLogDirectory;

    private readonly TextWriter _error;
    private readonly TextWriter _output;
    private readonly string _telemetryPath;

    public string Name => "logs";

    public LogsCommand(TextWriter output, TextWriter error, string? telemetryPath = null)
    {
        _output = output;
        _error = error;
        _telemetryPath = telemetryPath ?? Path.Combine(LogDir, "telemetry.txt");
    }

    /// <summary>
    ///     Execute the parsed subcommand.
    /// </summary>
    /// <param name="args">Args after the <c>logs</c> keyword.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Process exit code (0 = success, 1 = usage error, 2 = IO error).</returns>
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (args.Length == 0 || args[0] is "--list" or "-l")
            return await ListFilesAsync(all: args.Length > 0).ConfigureAwait(false);

        switch (args[0].ToLowerInvariant())
        {
            case "--help" or "-h":
                PrintUsage();
                return 0;
            case "--last" or "-n":
                return await PrintLastAsync(follow: false, ct).ConfigureAwait(false);
            case "--follow" or "-f":
                return await PrintLastAsync(follow: true, ct).ConfigureAwait(false);
            case "--clean" or "-c":
                return await CleanAsync(ct).ConfigureAwait(false);
            case "--spans" or "-s":
                return PrintSpans(args, ct);
            default:
                _error.WriteLine($"Unknown subcommand: {args[0]}");
                PrintUsage();
                return 1;
        }
    }

    private void PrintUsage()
    {
        _output.WriteLine("""
                          harbor logs — view and manage per-run log files.
                          Logs are stored in: ~/.harbor/logs/  (one file per run)

                          Usage:
                            harbor logs                List the 10 most recent log files
                            harbor logs --list         List all log files (newest first)
                            harbor logs --last         Print the most recent log file
                            harbor logs --follow       Tail the most recent log file (Ctrl-C to exit)
                            harbor logs --clean        Delete all log files (prompts for confirmation)
                            harbor logs --spans        Show the slowest telemetry spans
                              --slow MS                Only spans at or above MS milliseconds (default: 0)
                              --top N                  Keep the N slowest spans (default: 10)
                            harbor logs --help         Show this help

                          Shorthands: -l, -n, -f, -c, -s, -h.
                          """);
    }

    private async Task<int> ListFilesAsync(bool all)
    {
        if (!Directory.Exists(LogDir))
        {
            _output.WriteLine($"No log directory yet: {LogDir}");
            return 0;
        }
        FileInfo[] files;
        try
        {
            files = Directory.GetFiles(LogDir, "harbor-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToArray();
        }
        catch (UnauthorizedAccessException ex)
        {
            _error.WriteLine($"Cannot read {LogDir}: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Cannot read {LogDir}: {ex.Message}");
            return 2;
        }

        if (files.Length == 0)
        {
            _output.WriteLine($"No log files in {LogDir}.");
            _output.WriteLine("Run any harbor command first — each run writes its own file.");
            return 0;
        }

        _output.WriteLine($"Log directory: {LogDir}");
        _output.WriteLine($"Files: {files.Length}{(all ? "" : " (showing 10 newest — use --list for all)")}");
        _output.WriteLine();
        int take = all ? files.Length : Math.Min(10, files.Length);
        int index = 1;
        foreach (var f in files.Take(take))
        {
            string size = f.Length switch
            {
                < 1024 => $"{f.Length} B",
                < 1024 * 1024 => $"{f.Length / 1024.0:F1} KB",
                _ => $"{f.Length / (1024.0 * 1024):F1} MB"
            };
            _output.WriteLine($"  {index,3}. {f.Name}   ({size}, {f.CreationTimeUtc:yyyy-MM-dd HH:mm:ss}Z)");
            index++;
        }
        return 0;
    }

    private async Task<int> PrintLastAsync(bool follow, CancellationToken ct)
    {
        if (!Directory.Exists(LogDir))
        {
            _error.WriteLine($"No log directory yet: {LogDir}");
            return 0;
        }
        Maybe<FileInfo> latest;
        try
        {
            latest = Directory.GetFiles(LogDir, "harbor-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .TryFirst();
        }
        catch (UnauthorizedAccessException ex)
        {
            _error.WriteLine($"Cannot read {LogDir}: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Cannot read {LogDir}: {ex.Message}");
            return 2;
        }

        if (latest.HasNoValue)
        {
            _error.WriteLine($"No log files in {LogDir}.");
            return 2;
        }

        _output.WriteLine($"=== {latest.Value.FullName} ===");
        try
        {
            using var fs = new FileStream(latest.Value.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                _output.WriteLine(line);
            }
            _output.Flush();

            if (!follow)
                return 0;

            _output.WriteLine("--- following (Ctrl-C to exit) ---");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                line = reader.ReadLine();
                if (line is not null)
                {
                    _output.WriteLine(line);
                    _output.Flush();
                }
                else
                {
                    try
                    {
                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return 0;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Cannot read {latest.Value.FullName}: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    ///     Print the slowest finished spans from the telemetry export. The
    ///     export is opt-in, so a missing file is reported as an empty result
    ///     with a hint rather than as an error.
    /// </summary>
    private Task<int> PrintSpans(string[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        double slowMs = 0;
        int top = 10;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--slow":
                    if (i + 1 >= args.Length || !TryParseMilliseconds(args[++i], out slowMs))
                    {
                        _error.WriteLine("--slow needs a non-negative millisecond value (for example: --slow 100).");
                        return Task.FromResult(1);
                    }
                    break;
                case "--top":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out top)
                        || top <= 0)
                    {
                        _error.WriteLine("--top needs a positive integer (for example: --top 10).");
                        return Task.FromResult(1);
                    }
                    break;
                default:
                    _error.WriteLine($"Unknown option for --spans: {args[i]}");
                    PrintUsage();
                    return Task.FromResult(1);
            }
        }

        if (!File.Exists(_telemetryPath))
        {
            _output.WriteLine($"No telemetry export yet: {_telemetryPath}");
            _output.WriteLine("Set HARBOR_TELEMETRY_EXPORT=file to record spans, then run a session.");
            return Task.FromResult(0);
        }

        IReadOnlyList<SpanLogReader.SpanEntry> entries;
        try
        {
            entries = SpanLogReader.Read(_telemetryPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _error.WriteLine($"Cannot read {_telemetryPath}: {ex.Message}");
            return Task.FromResult(2);
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Cannot read {_telemetryPath}: {ex.Message}");
            return Task.FromResult(2);
        }

        List<SpanLogReader.SpanEntry> slow = entries
            .Where(e => e.DurationMs >= slowMs)
            .OrderByDescending(e => e.DurationMs)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(top)
            .ToList();

        _output.WriteLine($"Span log: {_telemetryPath}");
        _output.WriteLine($"Spans: {entries.Count} total, showing {slow.Count} at or above {slowMs.ToString("F1", CultureInfo.InvariantCulture)} ms (slowest first).");
        _output.WriteLine();
        for (int i = 0; i < slow.Count; i++)
        {
            SpanLogReader.SpanEntry e = slow[i];
            _output.WriteLine($"  {i + 1,3}. {e.DurationMs.ToString("F3", CultureInfo.InvariantCulture),10} ms  {e.Name}  status={e.Status}");
        }

        return Task.FromResult(0);
    }

    /// <summary>Accept a bare number or a number with an <c>ms</c> suffix.</summary>
    private static bool TryParseMilliseconds(string raw, out double milliseconds)
    {
        string text = raw.Trim();
        if (text.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^2];
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds)
            && milliseconds >= 0
            && !double.IsNaN(milliseconds)
            && !double.IsInfinity(milliseconds);
    }

    private async Task<int> CleanAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(LogDir))
        {
            _output.WriteLine($"No log directory yet: {LogDir}");
            return 0;
        }
        FileInfo[] files;
        try
        {
            files = Directory.GetFiles(LogDir, "harbor-*.log")
                .Select(f => new FileInfo(f))
                .ToArray();
        }
        catch (UnauthorizedAccessException ex)
        {
            _error.WriteLine($"Cannot read {LogDir}: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Cannot read {LogDir}: {ex.Message}");
            return 2;
        }

        if (files.Length == 0)
        {
            _output.WriteLine("No log files to delete.");
            return 0;
        }

        _output.WriteLine($"About to delete {files.Length} log file(s) from {LogDir}.");
        _output.Write("Proceed? [y/N] ");
        _output.Flush();
        string? answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            _output.WriteLine("Cancelled.");
            return 0;
        }

        int deleted = 0;
        int skipped = 0;
        foreach (var f in files)
        {
            try
            {
                f.Delete();
                deleted++;
            }
            catch (IOException)
            {
                skipped++;
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        _output.WriteLine($"Deleted {deleted} file(s); skipped {skipped} (in use or permission denied).");
        return 0;
    }
}
