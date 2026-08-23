using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
namespace Harbor.Cli.Commands;

public sealed class DaemonCommand : ICommand
{
    private static readonly string HarborDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
    private static readonly string PidFile = Path.Combine(HarborDir, "daemon.pid");

    private readonly TextWriter _error;
    private readonly TextWriter _output;

    public DaemonCommand(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    public string Name => "daemon";

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string subcommand = args[0].ToLowerInvariant();
        if (subcommand == "start") return await StartAsync(ct).ConfigureAwait(false);
        if (subcommand == "stop") return await StopAsync(ct).ConfigureAwait(false);
        if (subcommand == "status") return await StatusAsync(ct).ConfigureAwait(false);
        _error.WriteLine($"Unknown subcommand: {subcommand}");
        PrintUsage();
        return 1;
    }

    private async Task<int> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsRunning())
        {
            _output.WriteLine("Daemon is already running.");
            return 0;
        }

        // Spawn THIS executable with --headless: Program.Main routes that flag
        // to the headless IPC host (full agent services + IPC server, no UI,
        // no REPL). Spawning by name ("harbor") fails whenever the binary is
        // not on PATH — and spawning anything that falls through to the
        // interactive REPL would leave a pid file pointing at a fake daemon.
        string selfPath = Environment.ProcessPath ?? string.Empty;
        if (selfPath.Length == 0)
        {
            _error.WriteLine("Failed to start daemon: cannot resolve the running executable path.");
            return 1;
        }

        Directory.CreateDirectory(HarborDir);

        var psi = new ProcessStartInfo
        {
            FileName = selfPath,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--headless");
        // The daemon exists to serve remote clients over IPC.
        psi.Environment["HARBOR_MODE"] = "ipc-server";

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Failed to start daemon: {ex.Message}");
            return 1;
        }
        if (process is null)
        {
            _error.WriteLine("Failed to start daemon process.");
            return 1;
        }

        using (process)
        {
            File.WriteAllText(PidFile, process.Id.ToString(CultureInfo.InvariantCulture));

            // Drain the child's console output so pipe buffers cannot fill up and
            // deadlock the daemon once it writes more than the OS pipe capacity.
            // (The daemon logs to ~/.harbor/logs; this is console-logger spill.)
            Task outDrain = process.StandardOutput.ReadToEndAsync();
            Task errDrain = process.StandardError.ReadToEndAsync();
            _ = Task.WhenAll(outDrain, errDrain).ContinueWith(
                t => _error.WriteLine($"Daemon output drain faulted: {t.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

            // Short grace period: if the child dies during startup (bad config,
            // crash) report failure now instead of leaving a pid file that points
            // at an already-dead process.
            using var startupGrace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            bool exitedDuringStartup;
            try
            {
                await process.WaitForExitAsync(startupGrace.Token).ConfigureAwait(false);
                exitedDuringStartup = true;
            }
            catch (OperationCanceledException)
            {
                exitedDuringStartup = false;   // still alive after the grace period
            }

            if (exitedDuringStartup)
            {
                int exitCode = process.ExitCode;
                try { File.Delete(PidFile); } catch { /* best-effort cleanup */ }
                _error.WriteLine($"Daemon exited during startup (exit code {exitCode}). See ~/.harbor/logs for details.");
                return 1;
            }

            _output.WriteLine($"Daemon started with PID {process.Id}.");
            return 0;
        }
    }

    private async Task<int> StopAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(PidFile))
        {
            _output.WriteLine("No daemon PID file found.");
            return 0;
        }

        string pidText = File.ReadAllText(PidFile).Trim();
        if (!int.TryParse(pidText, out int pid))
        {
            _error.WriteLine($"Invalid PID in {PidFile}: {pidText}");
            File.Delete(PidFile);
            return 1;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Failed to stop daemon: {ex.Message}");
            return 1;
        }
        finally
        {
            try { File.Delete(PidFile); } catch { /* best-effort cleanup */ }
        }

        _output.WriteLine($"Daemon (PID {pid}) stopped.");
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(PidFile))
        {
            _output.WriteLine("Daemon is not running (no PID file).");
            return 0;
        }

        string pidText = File.ReadAllText(PidFile).Trim();
        if (!int.TryParse(pidText, out int pid))
        {
            _output.WriteLine("Daemon status: unknown (invalid PID file).");
            return 0;
        }

        if (IsProcessRunning(pid))
        {
            _output.WriteLine($"Daemon is running (PID {pid}).");
        }
        else
        {
            _output.WriteLine($"Daemon is not running (PID {pid} not found).");
            try { File.Delete(PidFile); } catch { /* best-effort cleanup */ }
        }

        return 0;
    }

    private static bool IsRunning()
    {
        if (!File.Exists(PidFile))
            return false;

        string pidText = File.ReadAllText(PidFile).Trim();
        if (!int.TryParse(pidText, out int pid))
            return false;

        return IsProcessRunning(pid);
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private const string UsageText =
        "harbor daemon — manage the background harbor daemon.\n\n" +
        "Usage:\n" +
        "  harbor daemon start   Start the daemon (spawns this executable with --headless)\n" +
        "  harbor daemon stop    Stop the running daemon\n" +
        "  harbor daemon status  Check daemon status\n";

    private void PrintUsage() => _output.Write(UsageText);
}
