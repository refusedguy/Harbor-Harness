using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Hooks;

/// <summary>
///     Default <see cref="IHookRunner" />: spawns each matching hook command
///     with the JSON payload on stdin and parses the verdict JSON on stdout.
/// </summary>
/// <remarks>
///     <para>
///         Spawn shape follows <c>WorkspaceInspector.RunGitAsync</c> (the
///         <c>ChildProcessPipeDrainRules</c> precedent): both redirected pipes
///         are drained with <c>ReadToEndAsync</c> before the async wait, the
///         whole tree is killed on timeout, and the synchronous wait is never
///         used.
///     </para>
///     <para>
///         Hook commands are the user's own (same trust as the <c>bash</c>
///         tool): stdin/stdout are piped, stderr is captured for diagnostics,
///         the environment is inherited.
///     </para>
/// </remarks>
public sealed class HookRunner : IHookRunner
{
    /// <summary>Ceiling when a hook entry sets no timeout (Claude default is 60s; hooks gate tool calls, so 30s).</summary>
    public static readonly TimeSpan DefaultHookTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Hard ceiling for one hook entry, even when configured higher.</summary>
    public static readonly TimeSpan MaxHookTimeout = TimeSpan.FromMinutes(5);

    private const int MaxStderrInReason = 500;

    private readonly HookConfig _config;
    private readonly ILogger<HookRunner> _logger;
    private readonly TimeSpan _defaultTimeout;
    private string? _lastLoadErrorLogged;

    /// <summary>Construct a hook runner.</summary>
    /// <param name="configPath">hooks.json override; defaults to <see cref="HookConfig.GetDefaultPath" />.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="defaultTimeout">Default per-hook ceiling; defaults to <see cref="DefaultHookTimeout" />.</param>
    public HookRunner(string? configPath = null, ILogger<HookRunner>? logger = null, TimeSpan? defaultTimeout = null)
    {
        _config = new HookConfig(configPath);
        _logger = logger ?? NullLogger<HookRunner>.Instance;
        _defaultTimeout = defaultTimeout ?? DefaultHookTimeout;
    }

    /// <inheritdoc />
    public async Task<HookVerdict> RunPreToolUseAsync(
        string toolName, JsonElement args, string sessionId, CancellationToken ct = default)
    {
        // A cancelled run never gets a verdict — cancellation propagates so
        // the dispatcher reports it honestly instead of collapsing into deny.
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<HookEntry> matches = GetHooks(HookEvents.PreToolUse, toolName);
        if (matches.Count == 0)
        {
            return HookVerdict.Allow;
        }

        JsonElement currentArgs = args;
        bool edited = false;
        string? askReason = null;

        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            HookEntry entry = matches[i];
            var payload = new HookPayload(HookEvents.PreToolUse, toolName, currentArgs, null, sessionId);
            HookAttempt attempt = await RunOneAsync(entry, payload, ct).ConfigureAwait(false);

            if (attempt.Verdict is null)
            {
                // Fail-closed: a hook that timed out, failed to start, or
                // printed garbage denies the tool call it was asked about.
                _logger.LogWarning(
                    "PreToolUse hook '{Command}' failed for tool {ToolName}; failing closed to Deny: {Error}",
                    entry.Command, toolName, attempt.Error);
                return new HookVerdict(HookDecision.Deny, attempt.Error, null);
            }

            HookVerdict verdict = attempt.Verdict;
            if (verdict.EditedArgs is { } editedArgs && editedArgs.ValueKind == JsonValueKind.Object)
            {
                currentArgs = editedArgs.Clone();
                edited = true;
            }

            switch (verdict.Decision)
            {
                case HookDecision.Deny:
                    _logger.LogInformation(
                        "PreToolUse hook '{Command}' denied tool {ToolName}: {Reason}",
                        entry.Command, toolName, verdict.Reason);
                    return new HookVerdict(HookDecision.Deny, verdict.Reason, null);
                case HookDecision.Ask:
                    askReason ??= verdict.Reason ?? $"Hook '{entry.Command}' requested confirmation.";
                    break;
                default:
                    break;
            }
        }

        if (askReason is not null)
        {
            return new HookVerdict(HookDecision.Ask, askReason, edited ? currentArgs : null);
        }

        return edited ? new HookVerdict(HookDecision.Allow, null, currentArgs) : HookVerdict.Allow;
    }

    /// <inheritdoc />
    public async Task RunPostToolUseAsync(
        string toolName, JsonElement args, ToolResult result, string sessionId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            IReadOnlyList<HookEntry> matches = GetHooks(HookEvents.PostToolUse, toolName);
            for (int i = 0; i < matches.Count; i++)
            {
                HookEntry entry = matches[i];
                var payload = new HookPayload(HookEvents.PostToolUse, toolName, args, result.Output, sessionId);
                HookAttempt attempt = await RunOneAsync(entry, payload, ct).ConfigureAwait(false);
                LogAdvisory(HookEvents.PostToolUse, entry, toolName, attempt);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "PostToolUse hooks skipped: run cancelled");
        }
        catch (Exception ex)
        {
            // Advisory by contract: a hook failure must never fail the tool call.
            _logger.LogWarning(ex, "PostToolUse hooks failed for tool {ToolName}", toolName);
        }
    }

    /// <inheritdoc />
    public async Task RunSessionEndAsync(string sessionId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            IReadOnlyList<HookEntry> matches = GetHooks(HookEvents.SessionEnd, null);
            for (int i = 0; i < matches.Count; i++)
            {
                HookEntry entry = matches[i];
                var payload = new HookPayload(HookEvents.SessionEnd, null, null, null, sessionId);
                HookAttempt attempt = await RunOneAsync(entry, payload, ct).ConfigureAwait(false);
                LogAdvisory(HookEvents.SessionEnd, entry, null, attempt);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "SessionEnd hooks skipped: run cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SessionEnd hooks failed for session {SessionId}", sessionId);
        }
    }

    private IReadOnlyList<HookEntry> GetHooks(string eventName, string? toolName)
    {
        IReadOnlyList<HookEntry> matches = _config.GetHooks(eventName, toolName);
        string? loadError = _config.LoadError;
        if (loadError is not null && loadError != _lastLoadErrorLogged)
        {
            _lastLoadErrorLogged = loadError;
            _logger.LogWarning("Hook config ignored: {Error}", loadError);
        }

        return matches;
    }

    /// <summary>
    ///     Advisory-event trace: stdout stays a debug record, a hook that never
    ///     decided is a warning. Nothing here reaches the model or fails a run.
    /// </summary>
    private void LogAdvisory(string eventName, HookEntry entry, string? toolName, HookAttempt attempt)
    {
        if (attempt.Error is not null)
        {
            _logger.LogWarning(
                "{EventName} hook '{Command}' failed (tool {ToolName}): {Error}",
                eventName, entry.Command, toolName ?? "(none)", attempt.Error);
            return;
        }

        if (attempt.Stdout.Length > 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "{EventName} hook '{Command}' output (tool {ToolName}): {Output}",
                eventName, entry.Command, toolName ?? "(none)", attempt.Stdout);
        }
    }

    /// <summary>One hook process execution: spawn, pipe, wait, parse.</summary>
    private async Task<HookAttempt> RunOneAsync(HookEntry entry, HookPayload payload, CancellationToken ct)
    {
        string fileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        psi.ArgumentList.Add(entry.Command);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.Exited += (_, _) => exited.TrySetResult(true);

        string payloadJson;
        try
        {
            payloadJson = JsonSerializer.Serialize(payload, HookJsonContext.Default.HookPayload);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is NotSupportedException)
        {
            return HookAttempt.Failed($"Cannot serialize hook payload: {ex.Message}", "", "");
        }

        TimeSpan timeout = ResolveTimeout(entry);
        bool started;
        try
        {
            started = proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception
            || ex is FileNotFoundException
            || ex is InvalidOperationException
            || ex is UnauthorizedAccessException
            || ex is PlatformNotSupportedException)
        {
            return HookAttempt.Failed($"Cannot start hook '{entry.Command}': {ex.Message}", "", "");
        }

        if (!started)
        {
            return HookAttempt.Failed($"Cannot start hook '{entry.Command}': process failed to start.", "", "");
        }

        Task<string> stdoutTask;
        Task<string> stderrTask;
        try
        {
            await proc.StandardInput.WriteAsync(payloadJson.AsMemory(), ct).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            proc.StandardInput.Close();
            stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            stderrTask = proc.StandardError.ReadToEndAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KillQuietly(proc);
            throw;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        Task done = await Task.WhenAny(exited.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(false);
        if (!ReferenceEquals(done, exited.Task))
        {
            KillQuietly(proc);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _ = ex;
            }

            ct.ThrowIfCancellationRequested();
            // The kill closed the pipes, so completed readers hold whatever
            // the hook printed before it was killed — kept for the log.
            string partialOut = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            string partialErr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            return HookAttempt.Failed(
                $"Hook '{entry.Command}' timed out after {timeout.TotalSeconds:0.#}s and was killed.",
                partialOut,
                partialErr);
        }

        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return ParseVerdict(entry, stdout, stderr, proc.ExitCode);
    }

    private TimeSpan ResolveTimeout(HookEntry entry)
    {
        if (entry.TimeoutSeconds is int seconds && seconds > 0)
        {
            TimeSpan configured = TimeSpan.FromSeconds(seconds);
            return configured > MaxHookTimeout ? MaxHookTimeout : configured;
        }

        return _defaultTimeout.TotalSeconds <= 0 ? DefaultHookTimeout : _defaultTimeout;
    }

    private static HookAttempt ParseVerdict(HookEntry entry, string stdout, string stderr, int exitCode)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            // Side-effect-only hook (exit 0, silent) means allow — the Claude
            // shape. A non-zero exit with no verdict is fail-closed deny: the
            // hook died before deciding.
            return exitCode == 0
                ? new HookAttempt(new HookVerdict(HookDecision.Allow, null, null), null, stdout, stderr)
                : new HookAttempt(null,
                    $"Hook '{entry.Command}' exited with code {exitCode} and no verdict. Stderr: {Clip(stderr)}",
                    stdout, stderr);
        }

        HookVerdictDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(stdout, HookJsonContext.Default.HookVerdictDto);
        }
        catch (JsonException ex)
        {
            return new HookAttempt(null,
                $"Hook '{entry.Command}' printed invalid verdict JSON: {ex.Message}. Stderr: {Clip(stderr)}",
                stdout, stderr);
        }

        if (dto is null)
        {
            return new HookAttempt(null, $"Hook '{entry.Command}' printed a null verdict.", stdout, stderr);
        }

        JsonElement? editedArgs = dto.EditedArgs is { } raw && raw.ValueKind == JsonValueKind.Object
            ? raw.Clone()
            : null;

        string decision = dto.Decision?.Trim() ?? "";
        if (decision.Length == 0)
        {
            // No decision named: allow (with edit when args were supplied).
            return new HookAttempt(new HookVerdict(HookDecision.Allow, null, editedArgs), null, stdout, stderr);
        }

        if (decision.Equals("allow", StringComparison.OrdinalIgnoreCase))
        {
            return new HookAttempt(new HookVerdict(HookDecision.Allow, null, editedArgs), null, stdout, stderr);
        }

        if (decision.Equals("deny", StringComparison.OrdinalIgnoreCase))
        {
            return new HookAttempt(
                new HookVerdict(HookDecision.Deny, dto.Reason ?? $"Hook '{entry.Command}' denied the call.", null),
                null, stdout, stderr);
        }

        if (decision.Equals("ask", StringComparison.OrdinalIgnoreCase))
        {
            return new HookAttempt(
                new HookVerdict(HookDecision.Ask, dto.Reason ?? $"Hook '{entry.Command}' requested confirmation.", null),
                null, stdout, stderr);
        }

        if (decision.Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            // "edit" without an args object is malformed — fail closed rather
            // than silently running the unedited call.
            return editedArgs is not null
                ? new HookAttempt(new HookVerdict(HookDecision.Allow, null, editedArgs), null, stdout, stderr)
                : new HookAttempt(null,
                    $"Hook '{entry.Command}' declared edit but supplied no editedArgs object.", stdout, stderr);
        }

        return new HookAttempt(null,
            $"Hook '{entry.Command}' returned unknown decision '{dto.Decision}'. Stderr: {Clip(stderr)}",
            stdout, stderr);
    }

    private static void KillQuietly(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Best-effort kill: already-exited is the normal race.
            _ = ex;
        }
    }

    private static string Clip(string text)
    {
        string t = text.Trim();
        if (t.Length == 0)
        {
            return "(no stderr)";
        }

        return t.Length <= MaxStderrInReason ? t : t[..MaxStderrInReason] + "…";
    }

    /// <summary>Outcome of one hook process execution.</summary>
    /// <param name="Verdict">Parsed verdict; null when the hook never decided (fail-closed for PreToolUse).</param>
    /// <param name="Error">Why <see cref="Verdict" /> is null (start failure, timeout, bad JSON).</param>
    /// <param name="Stdout">Captured stdout (advisory-event debug trace).</param>
    /// <param name="Stderr">Captured stderr (advisory-event debug trace).</param>
    private sealed record HookAttempt(HookVerdict? Verdict, string? Error, string Stdout, string Stderr)
    {
        public static HookAttempt Failed(string error, string stdout, string stderr) =>
            new(null, error, stdout, stderr);
    }
}
