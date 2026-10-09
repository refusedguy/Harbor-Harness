using System.Diagnostics;
using System.Globalization;

namespace Harbor.Application.Sessions;

/// <summary>
///     Workspace materializer (epic #42, stage S2): turns the S1
///     <see cref="WorkspaceContract" /> into a real isolated working copy — a
///     detached git worktree — and persists a run manifest beside it so later
///     slices (accept/reject/report) can act from a different process.
/// </summary>
/// <remarks>
///     <para>
///         Isolation is <c>git worktree add --detach</c> only. No clone, no
///         copy, no writes anywhere under <c>contract.RepoRoot</c> (git's own
///         administrative bookkeeping under <c>.git/worktrees/</c> is inherent
///         to the command, not a write this code performs).
///     </para>
///     <para>
///         Fail-closed: any non-zero git exit, timeout or verification mismatch
///         yields <c>Result.Failure</c> and the error path attempts
///         <c>git worktree remove</c> so no half-registered worktree is left
///         behind. <c>Copy</c> isolation fails explicitly — it never silently
///         falls back to working in the caller's tree.
///     </para>
///     <para>
///         The runs root is resolved exactly like
///         <c>Harbor.Hosting.HarborPaths.GetHarborHome()</c> (<c>HARBOR_HOME</c>
///         or <c>~/.harbor</c>) but WITHOUT referencing the Hosting project:
///         Application depends on Domain only, so the five lines live here
///         rather than behind a new <c>ProjectReference</c>.
///     </para>
/// </remarks>
public static class WorkspaceMaterializer
{
    private static readonly TimeSpan AddTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RemoveTimeout = TimeSpan.FromSeconds(30);
    private const int MaxStderrInError = 500;

    /// <summary>
    ///     Materialize an isolated worktree for <paramref name="contract" />,
    ///     minting a fresh <see cref="RunId" />. The id is never user input.
    /// </summary>
    public static Task<Result<IsolatedWorkspace>> MaterializeAsync(
        WorkspaceContract contract,
        CancellationToken ct = default) =>
        MaterializeAsync(contract, RunId.New(), ct);

    /// <summary>
    ///     Materialize an isolated worktree for <paramref name="contract" />
    ///     under the given <paramref name="runId" />. The id stays a
    ///     <see cref="RunId" /> value object (never a raw user path); the
    ///     overload exists so tests can pin the id and pre-plant collisions.
    /// </summary>
    public static async Task<Result<IsolatedWorkspace>> MaterializeAsync(
        WorkspaceContract contract,
        RunId runId,
        CancellationToken ct = default)
    {
        if (contract is null)
            return Result.Failure<IsolatedWorkspace>("Workspace contract must not be null.");
        if (runId is null)
            return Result.Failure<IsolatedWorkspace>("Run id must not be null.");

        if (contract.Isolation != WorkspaceIsolation.Worktree)
            return Result.Failure<IsolatedWorkspace>(
                $"WorkspaceIsolation.{contract.Isolation} materialization is not implemented in v1 " +
                "(S2 supports git worktrees only) — refusing to fall back to working in the caller's tree.");

        string runDir = RunDir(runId.Value);
        string manifestPath = Path.Combine(runDir, "manifest.json");
        string worktreePath = Path.Combine(runDir, "worktree");

        if (File.Exists(manifestPath))
            return Result.Failure<IsolatedWorkspace>(
                $"Run '{runId.Value}' AlreadyExists: manifest at '{manifestPath}' — refusing to overwrite.");
        if (Directory.Exists(worktreePath))
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(worktreePath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Result.Failure<IsolatedWorkspace>(
                    $"Cannot inspect worktree path '{worktreePath}': {ex.Message}");
            }
            if (entries.Length > 0)
                return Result.Failure<IsolatedWorkspace>(
                    $"Worktree path '{worktreePath}' already exists and is not empty — refusing to overwrite.");
            try
            {
                Directory.Delete(worktreePath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Result.Failure<IsolatedWorkspace>(
                    $"Cannot remove empty worktree path '{worktreePath}': {ex.Message}");
            }
        }

        try
        {
            Directory.CreateDirectory(runDir);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Result.Failure<IsolatedWorkspace>(
                $"Cannot create run directory '{runDir}': {ex.Message}");
        }

        Result<GitOutput> added;
        try
        {
            added = await RunGitAsync(
                contract.RepoRoot,
                ["worktree", "add", "--detach", worktreePath, contract.BaseRevision],
                AddTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return Result.Failure<IsolatedWorkspace>($"Materialization of run '{runId.Value}' was cancelled.");
        }
        if (added.IsFailure)
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return added.ConvertFailure<IsolatedWorkspace>();
        }
        if (added.Value.ExitCode != 0)
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return Result.Failure<IsolatedWorkspace>(
                $"git worktree add failed for run '{runId.Value}': {Clip(added.Value.Stderr)}");
        }

        Result<GitOutput> verified;
        try
        {
            verified = await RunGitAsync(worktreePath, ["rev-parse", "HEAD"], ProbeTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return Result.Failure<IsolatedWorkspace>($"Materialization of run '{runId.Value}' was cancelled.");
        }
        if (verified.IsFailure)
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return verified.ConvertFailure<IsolatedWorkspace>();
        }
        string actual = verified.Value.Stdout.Trim();
        if (verified.Value.ExitCode != 0 || !actual.Equals(contract.BaseRevision, StringComparison.Ordinal))
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return Result.Failure<IsolatedWorkspace>(
                $"Worktree at '{worktreePath}' is not at the pinned revision: " +
                $"expected {contract.BaseRevision}, git reports '{actual}'.");
        }

        var manifest = new RunManifest(
            runId.Value,
            contract.RepoRoot,
            contract.BaseRevision,
            contract.BaseBranch,
            contract.Isolation,
            contract.Limits.TimeoutSeconds,
            contract.Limits.MaxSteps,
            worktreePath,
            RunState.Isolated,
            DateTimeOffset.UtcNow);
        Result<string> written = WriteManifestAtomic(manifestPath, manifest, overwrite: false);
        if (written.IsFailure)
        {
            await CleanupAsync(contract.RepoRoot, worktreePath, runDir).ConfigureAwait(false);
            return written.ConvertFailure<IsolatedWorkspace>();
        }

        return Result.Success(new IsolatedWorkspace(runId, worktreePath, contract.BaseRevision));
    }

    /// <summary>
    ///     Load the manifest for <paramref name="runIdValue" />. Unknown ids
    ///     yield <c>Result.Failure</c> — never null, never a default record.
    /// </summary>
    public static Result<RunManifest> TryLoad(string? runIdValue)
    {
        Result<string> id = ValidateRunId(runIdValue);
        if (id.IsFailure)
            return id.ConvertFailure<RunManifest>();

        string manifestPath = Path.Combine(RunDir(id.Value), "manifest.json");
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(manifestPath);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return Result.Failure<RunManifest>($"Unknown run '{id.Value}': no manifest at '{manifestPath}'.");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            return Result.Failure<RunManifest>($"Manifest for run '{id.Value}' is not valid JSON: {ex.Message}");
        }
        using (doc)
        {
            return ReadManifest(id.Value, doc.RootElement);
        }
    }

    /// <summary>
    ///     Release the run: <c>git worktree remove --force</c> plus
    ///     <c>State=Released</c>. Idempotent — releasing an already-released
    ///     run is a no-op success, followed by <c>git worktree prune</c> so no
    ///     dangling registration survives.
    /// </summary>
    public static async Task<Result<RunManifest>> ReleaseAsync(
        string? runIdValue,
        CancellationToken ct = default)
    {
        Result<RunManifest> loaded = TryLoad(runIdValue);
        if (loaded.IsFailure)
            return loaded;
        RunManifest manifest = loaded.Value;

        if (manifest.State == RunState.Released)
        {
            await PruneAsync(manifest.RepoRoot).ConfigureAwait(false);
            return Result.Success(manifest);
        }

        if (Directory.Exists(manifest.WorktreePath))
        {
            Result<GitOutput> removed;
            try
            {
                removed = await RunGitAsync(
                    manifest.RepoRoot,
                    ["worktree", "remove", "--force", manifest.WorktreePath],
                    RemoveTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result.Failure<RunManifest>($"Release of run '{manifest.RunId}' was cancelled.");
            }
            if (removed.IsFailure)
                return removed.ConvertFailure<RunManifest>();
            if (removed.Value.ExitCode != 0)
                return Result.Failure<RunManifest>(
                    $"Failed to release worktree at '{manifest.WorktreePath}': {Clip(removed.Value.Stderr)}");
        }

        var released = manifest with { State = RunState.Released };
        string manifestPath = Path.Combine(RunDir(manifest.RunId), "manifest.json");
        Result<string> written = WriteManifestAtomic(manifestPath, released, overwrite: true);
        if (written.IsFailure)
            return written.ConvertFailure<RunManifest>();

        await PruneAsync(manifest.RepoRoot).ConfigureAwait(false);
        return Result.Success(released);
    }

    /// <summary>
    ///     All loadable manifests under the runs root, oldest first.
    ///     Unreadable entries are skipped — <c>run list</c> is an operator
    ///     view, not a verification gate.
    /// </summary>
    public static IReadOnlyList<RunManifest> ListRuns()
    {
        var manifests = new List<RunManifest>();
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(RunsRoot());
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            return manifests;
        }
        foreach (string dir in dirs)
        {
            Result<RunManifest> loaded = TryLoad(Path.GetFileName(dir));
            if (loaded.IsSuccess)
                manifests.Add(loaded.Value);
        }
        manifests.Sort(static (a, b) => a.CreatedAtUtc.CompareTo(b.CreatedAtUtc));
        return manifests;
    }

    internal static string GetHarborHome()
    {
        if (Environment.GetEnvironmentVariable("HARBOR_HOME") is { Length: > 0 } custom)
            return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor");
    }

    internal static string RunsRoot() => Path.Combine(GetHarborHome(), "runs");

    internal static string RunDir(string runId) => Path.Combine(RunsRoot(), runId);

    private static Result<string> ValidateRunId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Result.Failure<string>("Run ID cannot be empty.");
        if (value != Path.GetFileName(value) || value is "." or "..")
            return Result.Failure<string>($"Invalid run id '{value}': path separators are not allowed.");
        return Result.Success(value);
    }

    private static async Task CleanupAsync(string repoRoot, string worktreePath, string runDir)
    {
        try
        {
            await RunGitAsync(
                repoRoot,
                ["worktree", "remove", "--force", worktreePath],
                RemoveTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
        await PruneAsync(repoRoot).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(runDir))
                Directory.Delete(runDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static async Task PruneAsync(string repoRoot)
    {
        try
        {
            await RunGitAsync(repoRoot, ["worktree", "prune"], ProbeTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }

    private static Result<string> WriteManifestAtomic(string manifestPath, RunManifest manifest, bool overwrite)
    {
        byte[] bytes;
        try
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", 1);
                writer.WriteString("runId", manifest.RunId);
                writer.WriteString("repoRoot", manifest.RepoRoot);
                writer.WriteString("baseRevision", manifest.BaseRevision);
                if (manifest.BaseBranch is null)
                    writer.WriteNull("baseBranch");
                else
                    writer.WriteString("baseBranch", manifest.BaseBranch);
                writer.WriteString("isolation", manifest.Isolation.ToString());
                writer.WriteNumber("timeoutSeconds", manifest.TimeoutSeconds);
                writer.WriteNumber("maxSteps", manifest.MaxSteps);
                writer.WriteString("worktreePath", manifest.WorktreePath);
                writer.WriteString("state", manifest.State.ToString());
                writer.WriteString("createdAtUtc", manifest.CreatedAtUtc);
                writer.WriteEndObject();
            }
            bytes = ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Result.Failure<string>($"Cannot serialize manifest for run '{manifest.RunId}': {ex.Message}");
        }

        string tmp = manifestPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            return Result.Failure<string>($"Cannot write manifest for run '{manifest.RunId}': {ex.Message}");
        }
        try
        {
            File.Move(tmp, manifestPath, overwrite);
        }
        catch (IOException ex)
        {
            DeleteQuiet(tmp);
            return Result.Failure<string>(
                $"Run '{manifest.RunId}' AlreadyExists: manifest at '{manifestPath}' — refusing to overwrite. ({ex.Message})");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is DirectoryNotFoundException)
        {
            DeleteQuiet(tmp);
            return Result.Failure<string>($"Cannot write manifest for run '{manifest.RunId}': {ex.Message}");
        }
        return Result.Success(manifestPath);
    }

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _ = ex;
        }
    }

    private static Result<RunManifest> ReadManifest(string runId, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return Result.Failure<RunManifest>($"Manifest for run '{runId}' is not a JSON object.");
        string? fileRunId = GetString(root, "runId");
        string? repoRoot = GetString(root, "repoRoot");
        string? baseRevision = GetString(root, "baseRevision");
        string? worktreePath = GetString(root, "worktreePath");
        string? isolationRaw = GetString(root, "isolation");
        string? stateRaw = GetString(root, "state");
        if (string.IsNullOrEmpty(fileRunId) || string.IsNullOrEmpty(repoRoot)
            || string.IsNullOrEmpty(baseRevision) || string.IsNullOrEmpty(worktreePath)
            || string.IsNullOrEmpty(isolationRaw) || string.IsNullOrEmpty(stateRaw))
            return Result.Failure<RunManifest>($"Manifest for run '{runId}' is missing required fields.");
        if (!fileRunId.Equals(runId, StringComparison.Ordinal))
            return Result.Failure<RunManifest>(
                $"Manifest run id mismatch: directory '{runId}' but manifest says '{fileRunId}'.");
        if (!Enum.TryParse<WorkspaceIsolation>(isolationRaw, ignoreCase: true, out WorkspaceIsolation isolation)
            || !Enum.IsDefined(isolation))
            return Result.Failure<RunManifest>($"Manifest for run '{runId}' has unknown isolation '{isolationRaw}'.");
        if (!Enum.TryParse<RunState>(stateRaw, ignoreCase: true, out RunState state) || !Enum.IsDefined(state))
            return Result.Failure<RunManifest>($"Manifest for run '{runId}' has unknown state '{stateRaw}'.");
        if (!TryGetInt(root, "timeoutSeconds", out int timeoutSeconds)
            || !TryGetInt(root, "maxSteps", out int maxSteps))
            return Result.Failure<RunManifest>($"Manifest for run '{runId}' has invalid limits.");
        DateTimeOffset createdAtUtc;
        try
        {
            string? raw = GetString(root, "createdAtUtc");
            if (raw is null)
                return Result.Failure<RunManifest>($"Manifest for run '{runId}' is missing 'createdAtUtc'.");
            createdAtUtc = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }
        catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
        {
            return Result.Failure<RunManifest>($"Manifest for run '{runId}' has invalid 'createdAtUtc'.");
        }
        string? baseBranch = GetString(root, "baseBranch");
        return Result.Success(new RunManifest(
            fileRunId, repoRoot, baseRevision, baseBranch, isolation,
            timeoutSeconds, maxSteps, worktreePath, state, createdAtUtc));
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null,
        };
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out JsonElement el))
            return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int n))
        {
            value = n;
            return true;
        }
        return false;
    }

    private sealed record GitOutput(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    ///     One git invocation via argument list (never a shell string) with the
    ///     non-interactive environment mirroring the S1 probe helper.
    /// </summary>
    private static async Task<Result<GitOutput>> RunGitAsync(
        string workingDirectory, string[] argv, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in argv)
            psi.ArgumentList.Add(arg);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exited.TrySetResult(true);
        bool started;
        try
        {
            started = proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            || ex is FileNotFoundException
            || ex is InvalidOperationException
            || ex is UnauthorizedAccessException)
        {
            return Result.Failure<GitOutput>(
                $"Cannot start git {argv[0]} in '{workingDirectory}': {ex.Message} Is git installed and on PATH?");
        }
        if (!started)
            return Result.Failure<GitOutput>($"Cannot start git {argv[0]} in '{workingDirectory}': process failed to start.");

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = new CancellationTokenSource(timeout);
        Task done = await Task.WhenAny(exited.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);
        if (!ReferenceEquals(done, exited.Task))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
            return Result.Failure<GitOutput>(
                $"git {argv[0]} timed out after {timeout.TotalSeconds:N0}s in '{workingDirectory}' and was killed.");
        }

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return Result.Success(new GitOutput(proc.ExitCode, stdout, stderr));
    }

    private static string Clip(string text)
    {
        string t = text.Trim();
        if (t.Length == 0)
            return "(no stderr)";
        return t.Length <= MaxStderrInError ? t : t[..MaxStderrInError] + "…";
    }
}
