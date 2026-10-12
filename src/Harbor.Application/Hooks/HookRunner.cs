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
///     <para>
///         <b>Two shapes, one runner.</b> The gate loop
///         (<see cref="RunVerdictGateAsync" />) merges verdicts and is the
///         ONLY place fail-closed lives — both <c>PreToolUse</c> and
///         <c>UserPromptSubmit</c> go through it. The advisory loop
///         (<see cref="RunAdvisoryAsync" />) runs hooks for their side effects
///         and discards verdicts — the other four events go through it. A new
///         event point is therefore a constant plus one call, never a second
///         copy of the fail-closed rules.
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
    public Task<HookVerdict> RunPreToolUseAsync(
        string toolName, JsonElement args, string sessionId, CancellationToken ct = default)
    {
        // A cancelled run never gets a verdict — cancellation propagates so
        // the dispatcher reports it honestly instead of collapsing into deny.
        ct.ThrowIfCancellationRequested();

        return RunVerdictGateAsync(
            HookEvents.PreToolUse,
            new HookPayload(HookEvents.PreToolUse, toolName, args, null, sessionId),
            allowEdits: true, ct);
    }

    /// <summary>
    ///     The gate loop both blocking events share: run every matching hook,
    ///     fold their verdicts, fail closed when one never decided.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Merge order (slice 1, unchanged): the first deny wins
    ///         immediately; an ask is remembered and a LATER deny overrides it;
    ///         edits accumulate into the payload later hooks see, so a chain
    ///         of narrowing hooks composes instead of the last one winning.
    ///     </para>
    ///     <para>
    ///         <paramref name="allowEdits" /> is what tells the two callers
    ///         apart. <c>PreToolUse</c> lets a hook rewrite the args; a prompt
    ///         submit has nothing to rewrite, so a hook that answers
    ///         <c>edit</c> there is read as an allow rather than a fail-closed
    ///         error — the hook asked to proceed, and it did.
    ///     </para>
    /// </remarks>
    private async Task<HookVerdict> RunVerdictGateAsync(
        string eventName, HookPayload payload, bool allowEdits, CancellationToken ct)
    {
        IReadOnlyList<HookEntry> matches = GetHooks(eventName, payload.Tool);
        if (matches.Count == 0)
        {
            return HookVerdict.Allow;
        }

        HookPayload current = payload;
        bool edited = false;
        string? askReason = null;

        for (int i = 0; i < matches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            HookEntry entry = matches[i];
            HookAttempt attempt = await RunOneAsync(entry, current, allowEdits, ct).ConfigureAwait(false);

            if (attempt.Verdict is null)
            {
                // Fail-closed: a hook that timed out, failed to start, or
                // printed garbage denies the work it was asked about.
                HookRunnerLog.HookFailedDeny(_logger, eventName, entry.Command, attempt.Error);
                return new HookVerdict(HookDecision.Deny, attempt.Error, null);
            }

            HookVerdict verdict = attempt.Verdict;
            if (allowEdits
                && verdict.EditedArgs is { } editedArgs
                && editedArgs.ValueKind == JsonValueKind.Object)
            {
                current = current with { Args = editedArgs.Clone() };
                edited = true;
            }

            switch (verdict.Decision)
            {
                case HookDecision.Deny:
                    HookRunnerLog.HookDeniedCall(_logger, eventName, entry.Command, verdict.Reason);
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
            return new HookVerdict(HookDecision.Ask, askReason, edited ? current.Args : null);
        }

        return edited
            ? new HookVerdict(HookDecision.Allow, null, current.Args)
            : HookVerdict.Allow;
    }

    /// <inheritdoc />
    public async Task RunPostToolUseAsync(
        string toolName, JsonElement args, ToolResult result, string sessionId, CancellationToken ct = default)
    {
        var payload = new HookPayload(HookEvents.PostToolUse, toolName, args, result.Output, sessionId);
        await RunAdvisoryAsync(HookEvents.PostToolUse, toolName, payload, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RunSessionEndAsync(string sessionId, CancellationToken ct = default)
    {
        var payload = new HookPayload(HookEvents.SessionEnd, null, null, null, sessionId);
        return RunAdvisoryAsync(HookEvents.SessionEnd, null, payload, ct);
    }

    /// <inheritdoc />
    public Task RunSessionStartAsync(
        string sessionId, string? agent = null, string? model = null, CancellationToken ct = default)
    {
        var payload = new HookPayload(HookEvents.SessionStart, null, null, null, sessionId)
        {
            Agent = agent,
            Model = model,
        };
        return RunAdvisoryAsync(HookEvents.SessionStart, null, payload, ct);
    }

    /// <inheritdoc />
    public async Task<HookVerdict> RunUserPromptSubmitAsync(
        string sessionId, string prompt, string? agent = null, string? model = null,
        CancellationToken ct = default)
    {
        // Same cancellation contract as PreToolUse: a cancelled run reports
        // cancellation, never a hook deny that never happened.
        ct.ThrowIfCancellationRequested();

        var payload = new HookPayload(HookEvents.UserPromptSubmit, null, null, null, sessionId)
        {
            Agent = agent,
            Model = model,
            Prompt = prompt,
        };
        return await RunVerdictGateAsync(
            HookEvents.UserPromptSubmit, payload, allowEdits: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RunPreCompactAsync(
        string sessionId, string? agent = null, string? model = null, CancellationToken ct = default)
    {
        var payload = new HookPayload(HookEvents.PreCompact, null, null, null, sessionId)
        {
            Agent = agent,
            Model = model,
        };
        return RunAdvisoryAsync(HookEvents.PreCompact, null, payload, ct);
    }

    /// <summary>
    ///     The advisory loop the non-gating events share: run every matching
    ///     hook, log what it printed or how it failed, and return normally.
    /// </summary>
    /// <remarks>
    ///     Nothing here reaches the model or the run outcome. That is the
    ///     whole contract — a hook on <c>PostToolUse</c>, <c>SessionStart</c>,
    ///     <c>PreCompact</c> or <c>SessionEnd</c> is a notification, so a
    ///     broken one costs a log line, never a failed turn.
    /// </remarks>
    private async Task RunAdvisoryAsync(
        string eventName, string? toolName, HookPayload payload, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            IReadOnlyList<HookEntry> matches = GetHooks(eventName, toolName);
            for (int i = 0; i < matches.Count; i++)
            {
                HookEntry entry = matches[i];
                HookAttempt attempt = await RunOneAsync(entry, payload, allowEdits: false, ct).ConfigureAwait(false);
                LogAdvisory(eventName, entry, toolName, attempt);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            HookRunnerLog.HooksSkippedCancelled(_logger, ex, eventName);
        }
        catch (Exception ex)
        {
            // Advisory by contract: a hook failure must never fail the run.
            HookRunnerLog.HooksFailed(_logger, ex, eventName);
        }
    }

    private IReadOnlyList<HookEntry> GetHooks(string eventName, string? toolName)
    {
        IReadOnlyList<HookEntry> matches = _config.GetHooks(eventName, toolName);
        string? loadError = _config.LoadError;
        if (loadError is not null && loadError != _lastLoadErrorLogged)
        {
            _lastLoadErrorLogged = loadError;
            HookRunnerLog.HookConfigIgnored(_logger, loadError);
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
            HookRunnerLog.ToolHookFailed(_logger, eventName, entry.Command, toolName ?? "(none)", attempt.Error);
            return;
        }

        if (attempt.Stdout.Length > 0 && _logger.IsEnabled(LogLevel.Debug))
        {
            HookRunnerLog.ToolHookOutput(_logger, eventName, entry.Command, toolName ?? "(none)", attempt.Stdout);
        }
    }

    /// <summary>One hook process execution: spawn, pipe, wait, parse.</summary>
    private async Task<HookAttempt> RunOneAsync(HookEntry entry, HookPayload payload, bool allowEdits, CancellationToken ct)
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
        // The stdin write rides its own ceiling, not the run token: a hook
        // that never reads stdin must stall into a hook timeout, never hang
        // the run past it.
        using var stdinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stdinCts.CancelAfter(timeout);
        try
        {
            await proc.StandardInput.WriteAsync(payloadJson.AsMemory(), stdinCts.Token).ConfigureAwait(false);
            await proc.StandardInput.FlushAsync(stdinCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            KillQuietly(proc);
            throw;
        }
        catch (OperationCanceledException)
        {
            // The write stalled past the hook ceiling (the hook never reads
            // stdin): the same verdict as any other timeout.
            KillQuietly(proc);
            return HookAttempt.Failed(
                $"Hook '{entry.Command}' timed out after {timeout.TotalSeconds:0.#}s and was killed.", "", "");
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
        {
            // A fast hook that never reads stdin (echo/true) may exit before
            // the write lands — EPIPE then describes the race, not a failure.
            // Fall through to drain whatever it printed; the verdict parse
            // stays the gate (silence is still fail-closed deny).
            HookRunnerLog.HookStdinDrainRace(_logger, ex, entry.Command);
        }

        try
        {
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            _ = ex;
        }

        stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        stderrTask = proc.StandardError.ReadToEndAsync(ct);

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
        return ParseVerdict(entry, stdout, stderr, proc.ExitCode, allowEdits);
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

    private static HookAttempt ParseVerdict(HookEntry entry, string stdout, string stderr, int exitCode, bool allowEdits)
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
            if (!allowEdits)
            {
                // A prompt-submit hook has no args to rewrite: answering
                // `edit` means "reviewed, may proceed" — read as allow, never
                // fail-closed (IHookRunner.RunUserPromptSubmitAsync contract).
                return new HookAttempt(new HookVerdict(HookDecision.Allow, null, null), null, stdout, stderr);
            }

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

/// <summary>
///     SG2: BCL <c>[LoggerMessage]</c> delegates for <see cref="HookRunner" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class HookRunnerLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{EventName} hook '{Command}' failed; failing closed to Deny: {Error}")]
    public static partial void HookFailedDeny(ILogger logger, string eventName, string command, string? error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "{EventName} hook '{Command}' denied the call: {Reason}")]
    public static partial void HookDeniedCall(ILogger logger, string eventName, string command, string? reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "{EventName} hooks skipped: run cancelled")]
    public static partial void HooksSkippedCancelled(ILogger logger, Exception ex, string eventName);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "{EventName} hooks failed")]
    public static partial void HooksFailed(ILogger logger, Exception ex, string eventName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Hook config ignored: {Error}")]
    public static partial void HookConfigIgnored(ILogger logger, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "{EventName} hook '{Command}' failed (tool {ToolName}): {Error}")]
    public static partial void ToolHookFailed(ILogger logger, string eventName, string command, string toolName, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "{EventName} hook '{Command}' output (tool {ToolName}): {Output}")]
    public static partial void ToolHookOutput(ILogger logger, string eventName, string command, string toolName, string output);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "Hook '{Command}' exited before the stdin write landed; draining output")]
    public static partial void HookStdinDrainRace(ILogger logger, Exception ex, string command);

}
