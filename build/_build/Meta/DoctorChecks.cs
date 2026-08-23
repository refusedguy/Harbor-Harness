using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Harbor.Build.Meta;
/// <summary>Inputs the doctor needs from the build instance.</summary>
public sealed record DoctorContext(
    string RootDirectory,
    string TargetFramework,
    bool AotVariantRequested,
    bool ReleaseRequested,
    string ReleaseTag);
/// <summary>Result of one doctor check.</summary>
public sealed record CheckResult(string Id, string Status, string Detail, string? Fix = null)
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string NotApplicable = "na";
}
/// <summary>Aggregated doctor report: checks + verdict + exit code.</summary>
public sealed record DoctorReport(
    IReadOnlyList<CheckResult> Checks,
    string Verdict,
    int ExitCode)
{
    public const string VerdictOk = "ok";
    public const string VerdictDegraded = "degraded";
    public const string VerdictBroken = "broken";
}
/// <summary>
///     Offline environment diagnostics for <c>./build.sh doctor</c>. Checks
///     never throw and never touch the network (token presence is checked,
///     tokens are never validated). Check ids are a stable API — agents
///     build logic on them; new ids may be added but existing ones must not
///     be renamed.
/// </summary>
public static class DoctorChecks
{
    private const long WarnBytesThreshold = 10L * 1024 * 1024 * 1024; // 10 GB
    private const long FailBytesThreshold = 2L * 1024 * 1024 * 1024;  // 2 GB
    /// <summary>All stable check ids in canonical order.</summary>
    public static readonly string[] AllCheckIds =
    [
        "dotnet.sdk",
        "sdk.pin",
        "git.state",
        "disk.space",
        "tar.available",
        "gh.token",
        "release.args",
        "aot.toolchain",
        "solution.files",
        "bootstrap.cache",
        "node.pnpm"
    ];
    /// <summary>
    ///     Runs every check (or only the ids in <paramref name="checkFilter" />)
    ///     and aggregates the verdict. The caller validates the filter —
    ///     unknown ids never reach this method.
    /// </summary>
    public static DoctorReport RunAll(DoctorContext ctx, IReadOnlyCollection<string>? checkFilter = null)
    {
        var all = new List<CheckResult>
        {
            CheckDotnetSdk(ctx),
            CheckSdkPin(ctx),
            CheckGitState(ctx),
            CheckDiskSpace(ctx),
            CheckTarAvailable(),
            CheckGhToken(ctx),
            CheckReleaseArgs(ctx),
            CheckAotToolchain(ctx),
            CheckSolutionFiles(ctx),
            CheckBootstrapCache(ctx),
            CheckNodePnpm()
        };
        var checks = checkFilter is null
            ? all
            : all.Where(c => checkFilter.Contains(c.Id)).ToList();
        var hasFail = checks.Any(c => c.Status == CheckResult.Fail);
        var hasWarn = checks.Any(c => c.Status == CheckResult.Warn);
        var verdict = hasFail ? DoctorReport.VerdictBroken
            : hasWarn ? DoctorReport.VerdictDegraded
            : DoctorReport.VerdictOk;
        return new DoctorReport(checks, verdict, hasFail ? 3 : 0);
    }
    /// <summary>Serializes the report as one JSON document line.</summary>
    public static string ToJson(DoctorReport report)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("command", "doctor");
            writer.WriteString("verdict", report.Verdict);
            writer.WriteNumber("exitCode", report.ExitCode);
            writer.WriteStartArray("checks");
            foreach (var check in report.Checks)
            {
                writer.WriteStartObject();
                writer.WriteString("id", check.Id);
                writer.WriteString("status", check.Status);
                writer.WriteString("detail", check.Detail);
                if (check.Fix is not null)
                {
                    writer.WriteString("fix", check.Fix);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("recommendedCommands");
            writer.WriteStringValue("./build.sh Compile --dry-run");
            writer.WriteStringValue("./build.sh list --format json");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
    /// <summary>Renders the report as a human-readable table with fix hints.</summary>
    public static void RenderPretty(DoctorReport report, BuildOutput output)
    {
        output.Human($"Doctor — scope: local machine only, no network. Verdict: {report.Verdict} (exit {report.ExitCode})");
        foreach (var check in report.Checks)
        {
            var marker = check.Status switch
            {
                CheckResult.Ok => "[ ok ]",
                CheckResult.Warn => "[WARN]",
                CheckResult.Fail => "[FAIL]",
                _ => "[ na ]"
            };
            output.Human($"  {marker} {check.Id,-16} {check.Detail}");
            if (check.Fix is not null && check.Status != CheckResult.Ok)
            {
                output.Human($"         fix: {check.Fix}");
            }
        }
    }
    private static CheckResult CheckDotnetSdk(DoctorContext ctx)
    {
        var dotnet = FindDotnetExecutable();
        if (dotnet is null)
        {
            return new CheckResult(
                "dotnet.sdk", CheckResult.Fail, "dotnet executable not found (DOTNET_INSTALL_DIR, ~/.dotnet, PATH)",
                "Install the .NET SDK from https://dot.net or set DOTNET_INSTALL_DIR");
        }
        var result = RunProcess(dotnet, "--version", null, TimeSpan.FromSeconds(15));
        if (result is null)
        {
            return new CheckResult(
                "dotnet.sdk", CheckResult.Fail, $"dotnet found at {dotnet} but '--version' did not answer",
                "Verify the SDK installation (dotnet --info)");
        }
        var version = result.Value.stdout.Trim();
        if (TryGetTfmMajor(ctx.TargetFramework, out var expectedMajor) &&
            !version.StartsWith(expectedMajor.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return new CheckResult(
                "dotnet.sdk", CheckResult.Warn, $"{version} at {dotnet}, but solution expects .NET {expectedMajor}",
                $"Install the .NET {expectedMajor} SDK next to the current one");
        }
        return new CheckResult("dotnet.sdk", CheckResult.Ok, $"{version} at {dotnet}");
    }

    private static bool TryGetTfmMajor(string targetFramework, out int major)
    {
        major = 0;
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var rest = targetFramework[3..];
        var dot = rest.IndexOf('.');
        var digits = dot > 0 ? rest[..dot] : rest;
        return int.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out major);
    }
    private static CheckResult CheckSdkPin(DoctorContext ctx)
    {
        var path = System.IO.Path.Combine(ctx.RootDirectory, "global.json");
        if (!File.Exists(path))
        {
            return new CheckResult(
                "sdk.pin", CheckResult.Warn, "global.json does not pin an SDK version",
                "Add {\"sdk\":{\"version\":\"10.x\",\"rollForward\":\"latestFeature\"}} to global.json");
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                sdk.TryGetProperty("version", out var version))
            {
                return new CheckResult(
                    "sdk.pin", CheckResult.Ok, $"pinned to {version.GetString()} ({path})");
            }
        }
        catch (JsonException ex)
        {
            return new CheckResult("sdk.pin", CheckResult.Warn, $"global.json is not valid JSON ({ex.Message})");
        }
        return new CheckResult(
            "sdk.pin", CheckResult.Warn, "global.json exists but has no sdk.version pin",
            "Add {\"sdk\":{\"version\":\"10.x\",\"rollForward\":\"latestFeature\"}} to global.json");
    }
    private static CheckResult CheckGitState(DoctorContext ctx)
    {
        var git = FindOnPath(IsWindows() ? "git.exe" : "git");
        if (git is null)
        {
            return new CheckResult("git.state", CheckResult.NotApplicable, "git not on PATH");
        }
        var branchResult = RunProcess(git, "rev-parse --abbrev-ref HEAD", ctx.RootDirectory, TimeSpan.FromSeconds(10));
        var statusResult = RunProcess(git, "status --porcelain", ctx.RootDirectory, TimeSpan.FromSeconds(10));
        if (branchResult is null || statusResult is null)
        {
            return new CheckResult("git.state", CheckResult.Warn, "git commands failed; repository state unknown");
        }
        var lines = statusResult.Value.stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var dirty = lines.Count(l => !l.StartsWith("??", StringComparison.Ordinal));
        var untracked = lines.Length - dirty;
        if (dirty == 0 && untracked == 0)
        {
            return new CheckResult(
                "git.state", CheckResult.Ok, $"branch={branchResult.Value.stdout.Trim()} clean");
        }
        return new CheckResult(
            "git.state", CheckResult.Warn,
            $"branch={branchResult.Value.stdout.Trim()} dirty={dirty} untracked={untracked}",
            "Commit or stash before running Release");
    }
    private static CheckResult CheckDiskSpace(DoctorContext ctx)
    {
        try
        {
            var root = DriveInfo.GetDrives()
                .FirstOrDefault(d => ctx.RootDirectory.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase) && d.IsReady);
            if (root is null)
            {
                return new CheckResult("disk.space", CheckResult.NotApplicable, "could not resolve drive for the repository");
            }
            var free = root.AvailableFreeSpace;
            return free switch
            {
                < FailBytesThreshold => new CheckResult(
                    "disk.space", CheckResult.Fail, $"{root.Name} has {HumanBytes(free)} free",
                    "Free disk space — builds and artifacts need several GB"),
                < WarnBytesThreshold => new CheckResult(
                    "disk.space", CheckResult.Warn, $"{root.Name} has {HumanBytes(free)} free (< 10 GB)",
                    "Consider cleaning artifacts/ (./build.sh Clean)"),
                _ => new CheckResult("disk.space", CheckResult.Ok, $"{root.Name} has {HumanBytes(free)} free")
            };
        }
        catch (IOException ex)
        {
            return new CheckResult("disk.space", CheckResult.NotApplicable, $"drive query failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return new CheckResult("disk.space", CheckResult.NotApplicable, $"drive query failed: {ex.Message}");
        }
    }
    private static CheckResult CheckTarAvailable()
    {
        if (FindOnPath(IsWindows() ? "tar.exe" : "tar") is not null)
        {
            return new CheckResult("tar.available", CheckResult.Ok, "tar on PATH (used for TarGz archives)");
        }
        return new CheckResult(
            "tar.available", CheckResult.Warn, "tar binary not found on PATH",
            "Install tar (e.g. 'apt install tar') or use --archive Zip instead");
    }
    private static CheckResult CheckGhToken(DoctorContext ctx)
    {
        if (!ctx.ReleaseRequested)
        {
            return new CheckResult("gh.token", CheckResult.NotApplicable, "only needed for the Release target");
        }
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        return string.IsNullOrEmpty(token)
            ? new CheckResult(
                "gh.token", CheckResult.Warn, "GH_TOKEN not set — release upload will be skipped",
                "Export GH_TOKEN with a PAT having 'repo' scope to upload assets")
            : new CheckResult("gh.token", CheckResult.Ok, "GH_TOKEN set (presence only, not validated)");
    }
    private static CheckResult CheckReleaseArgs(DoctorContext ctx)
    {
        if (!ctx.ReleaseRequested)
        {
            return new CheckResult("release.args", CheckResult.NotApplicable, "only needed for the Release target");
        }
        return string.IsNullOrWhiteSpace(ctx.ReleaseTag)
            ? new CheckResult(
                "release.args", CheckResult.Fail, "--release-tag is empty but Release was requested",
                "Pass --release-tag v0.7.0")
            : new CheckResult("release.args", CheckResult.Ok, $"release-tag={ctx.ReleaseTag}");
    }
    private static CheckResult CheckAotToolchain(DoctorContext ctx)
    {
        if (!ctx.AotVariantRequested)
        {
            return new CheckResult("aot.toolchain", CheckResult.NotApplicable, "no AOT/Trimmed variant requested");
        }
        if (IsWindows())
        {
            var vswhere = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft Visual Studio", "Installer", "vswhere.exe");
            return File.Exists(vswhere)
                ? new CheckResult("aot.toolchain", CheckResult.Ok, "Visual Studio installer detected (MSVC toolchain assumed)")
                : new CheckResult(
                    "aot.toolchain", CheckResult.Warn, "MSVC toolchain not detected (vswhere missing)",
                    "Install Visual Studio 2022 with 'Desktop development with C++' for NativeAOT on Windows");
        }
        var compiler = FindOnPath("clang") ?? FindOnPath("clang-18") ?? FindOnPath("gcc");
        if (compiler is null)
        {
            return new CheckResult(
                "aot.toolchain", CheckResult.Fail, "no clang/gcc compiler on PATH",
                "Install clang and zlib headers: 'apt install clang zlib1g-dev'");
        }
        if (!File.Exists("/usr/include/zlib.h"))
        {
            return new CheckResult(
                "aot.toolchain", CheckResult.Warn, $"compiler {System.IO.Path.GetFileName(compiler)} found but zlib headers missing",
                "'apt install zlib1g-dev' (NativeAOT links against zlib)");
        }
        return new CheckResult(
            "aot.toolchain", CheckResult.Ok, $"compiler {System.IO.Path.GetFileName(compiler)} + zlib headers present");
    }
    private static CheckResult CheckSolutionFiles(DoctorContext ctx)
    {
        var required = new[]
        {
            System.IO.Path.Combine(ctx.RootDirectory, "Harbor.slnx"),
            System.IO.Path.Combine(ctx.RootDirectory, "build", "_build.csproj"),
            System.IO.Path.Combine(ctx.RootDirectory, ".nuke")
        };
        var missing = required.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        return missing.Count == 0
            ? new CheckResult("solution.files", CheckResult.Ok, "Harbor.slnx, build/_build.csproj, .nuke/ present")
            : new CheckResult(
                "solution.files", CheckResult.Fail, $"missing: {string.Join(", ", missing.Select(System.IO.Path.GetFileName))}",
                "Run from the repository root; restore deleted solution files");
    }
    private static CheckResult CheckBootstrapCache(DoctorContext ctx)
    {
        var dll = System.IO.Path.Combine(ctx.RootDirectory, ".nuke", "bin", "net10.0", "_build.dll");
        if (!File.Exists(dll))
        {
            return new CheckResult(
                "bootstrap.cache", CheckResult.Warn, ".nuke/bin/net10.0/_build.dll not built yet",
                "First ./build.sh run will compile the tool automatically (slower)");
        }
        var newestSource = Directory
            .EnumerateFiles(System.IO.Path.Combine(ctx.RootDirectory, "build"), "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".csproj", StringComparison.Ordinal))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        return File.GetLastWriteTimeUtc(dll) >= newestSource
            ? new CheckResult("bootstrap.cache", CheckResult.Ok, "build tool cache is fresh")
            : new CheckResult(
                "bootstrap.cache", CheckResult.Warn, "build sources changed since last tool build",
                "Bootstrap rebuilds the tool on the next ./build.sh run automatically");
    }
    private static CheckResult CheckNodePnpm()
    {
        return new CheckResult(
            "node.pnpm", CheckResult.NotApplicable,
            "repository does not use Node.js/pnpm (slot kept for future web assets)");
    }
    private static string? FindDotnetExecutable()
    {
        var exeName = IsWindows() ? "dotnet.exe" : "dotnet";
        var installDir = Environment.GetEnvironmentVariable("DOTNET_INSTALL_DIR");
        if (!string.IsNullOrEmpty(installDir))
        {
            var candidate = System.IO.Path.Combine(installDir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeCandidate = System.IO.Path.Combine(home, ".dotnet", exeName);
        if (File.Exists(homeCandidate))
        {
            return homeCandidate;
        }
        return FindOnPath(exeName);
    }
    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // malformed PATH entry — skip it
            }
        }
        return null;
    }
    private static (int exitCode, string stdout, string stderr)? RunProcess(
        string fileName, string arguments, string? workingDirectory, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }
            // Both streams are drained concurrently (async) to avoid the
            // classic full-pipe deadlock, then read after exit.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
    private static bool IsWindows() => OperatingSystem.IsWindows();
    private static string HumanBytes(long bytes) =>
        bytes >= FailBytesThreshold
            ? $"{(bytes / (1024.0 * 1024 * 1024)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} GB"
            : $"{(bytes / (1024.0 * 1024)).ToString("F0", System.Globalization.CultureInfo.InvariantCulture)} MB";
}
