using Harbor.Cli.Logging;
namespace Harbor.Cli.Commands;

/// <summary>
///     <c>harbor logs</c> — inspect per-run log files written by
///     <see cref="FileLoggerProvider"/> to <c>~/.harbor/logs/</c>.
/// </summary>
/// <remarks>
///     <para>
///         Subcommands:
///     </para>
///     <list type="table">
///         <item><term>(default)</term><description>List the 10 most recent log files (same as <c>--list</c>).</description></item>
///         <item><term>--list</term><description>List all log files, newest first.</description></item>
///         <item><term>--last</term><description>Print the most recent log file to stdout.</description></item>
///         <item><term>--follow</term><description>Print the most recent log file, then tail new lines (Ctrl-C to exit).</description></item>
///         <item><term>--clean</term><description>Delete every <c>harbor-*.log</c> file (after confirmation prompt).</description></item>
///         <item><term>--help</term><description>Show usage.</description></item>
///     </list>
///     <para>
///         <see cref="FileLoggerProvider"/> opens files with
///         <see cref="FileShare.Read"/>, so <c>--follow</c> and external
///         <c>tail -f</c> can read a file the CLI is actively writing to.
///     </para>
/// </remarks>
public sealed class LogsCommand
{
    private static readonly string LogDir = HarborLogManager.DefaultLogDirectory;

    private readonly TextWriter _error;
    private readonly TextWriter _output;

    /// <summary>
    ///     Create a <c>logs</c> command handler writing to <paramref name="output"/>
    ///     and <paramref name="error"/>.
    /// </summary>
    public LogsCommand(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    /// <summary>
    ///     Execute the parsed subcommand.
    /// </summary>
    /// <param name="args">Args after the <c>logs</c> keyword.</param>
    /// <returns>Process exit code (0 = success, 1 = usage error, 2 = IO error).</returns>
    public int Execute(string[] args)
    {
        if (args.Length == 0 || args[0] is "--list" or "-l")
            return ListFiles(all: args.Length > 0);

        switch (args[0].ToLowerInvariant())
        {
            case "--help" or "-h":
                PrintUsage();
                return 0;
            case "--last" or "-n":
                return PrintLast(follow: false);
            case "--follow" or "-f":
                return PrintLast(follow: true);
            case "--clean" or "-c":
                return Clean();
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
                            harbor logs --help         Show this help

                          Shorthands: -l, -n, -f, -c, -h.
                          """);
    }

    private int ListFiles(bool all)
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
        foreach (FileInfo f in files.Take(take))
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

    private int PrintLast(bool follow)
    {
        if (!Directory.Exists(LogDir))
        {
            _error.WriteLine($"No log directory yet: {LogDir}");
            return 2;
        }
        FileInfo? latest;
        try
        {
            latest = Directory.GetFiles(LogDir, "harbor-*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .FirstOrDefault();
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

        if (latest is null)
        {
            _error.WriteLine($"No log files in {LogDir}.");
            return 2;
        }

        _output.WriteLine($"=== {latest.FullName} ===");
        try
        {
            using var fs = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                _output.WriteLine(line);
            _output.Flush();

            if (!follow)
                return 0;

            // Tail new lines. FileLoggerProvider has AutoFlush=true, so new
            // writes are immediately visible to us (FileShare.Read on the
            // writer's side; we open with FileShare.ReadWrite).
            _output.WriteLine("--- following (Ctrl-C to exit) ---");
            while (true)
            {
                line = reader.ReadLine();
                if (line is not null)
                {
                    _output.WriteLine(line);
                    _output.Flush();
                }
                else
                {
                    Thread.Sleep(250);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException ex)
        {
            _error.WriteLine($"Cannot read {latest.FullName}: {ex.Message}");
            return 2;
        }
    }

    private int Clean()
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
        foreach (FileInfo f in files)
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
