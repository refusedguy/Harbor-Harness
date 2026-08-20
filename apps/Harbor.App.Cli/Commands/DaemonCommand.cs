using System.Diagnostics;
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

        Directory.CreateDirectory(HarborDir);

        var psi = new ProcessStartInfo
        {
            FileName = "harbor",
            Arguments = "--headless --remote",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                _error.WriteLine("Failed to start daemon process.");
                return 1;
            }

            File.WriteAllText(PidFile, process.Id.ToString());
            _output.WriteLine($"Daemon started with PID {process.Id}.");
            return 0;
        }
        catch (Exception ex)
        {
            _error.WriteLine($"Failed to start daemon: {ex.Message}");
            return 1;
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

    private static void PrintUsage()
    {
        Console.WriteLine("harbor daemon — manage the background harbor daemon.\n\nUsage:\n  harbor daemon start   Start the daemon (--headless --remote)\n  harbor daemon stop    Stop the running daemon\n  harbor daemon status  Check daemon status\n");
    }
}
