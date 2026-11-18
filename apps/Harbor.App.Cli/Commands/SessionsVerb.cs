using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Hosting;
using Harbor.Storage.Jsonl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor sessions</c> family: list (default), rename, retitle, export, export-zip, import,
///     search, usage, revert, checkpoint, checkpoints, rewind, tree, fork, maintenance.
///     Extracted from <c>Program</c> (#176);
///     rename persists a new title via ISessionStore.UpdateAsync,
///     export/import round-trip one session through the portable line payload
///     built by <c>ISessionPorter</c> (see StorageModule for the backend wiring),
///     export-zip writes the same session as ONE archive — log, attachments and
///     manifest — through <c>SessionArchiveWriter</c> (#1750).
///     Behavior is 1:1 with the former <c>Program.RunSessionsAsync*</c>.
/// </summary>
internal static class SessionsVerb
{
    internal static async Task<int> RunAsync(ILogger logger, string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
                return await RunListSessionsAsync(logger);
            case "rename":
                return await RunRenameSessionAsync(logger, args.Skip(1).ToArray());
            case "retitle":
                return await RunRetitleSessionAsync(args.Skip(1).ToArray());
            case "export":
                return await RunExportSessionAsync(logger, args.Skip(1).ToArray());
            case "export-zip":
                return await RunExportZipSessionAsync(logger, args.Skip(1).ToArray());
            case "export-md":
                return await RunExportFormattedSessionAsync(logger, args.Skip(1).ToArray(), SessionExportFormat.Markdown);
            case "export-json":
                return await RunExportFormattedSessionAsync(logger, args.Skip(1).ToArray(), SessionExportFormat.Json);
            case "import":
                return await RunImportSessionAsync(logger, args.Skip(1).ToArray());
            case "search":
                return await RunSearchSessionsAsync(args.Skip(1).ToArray());
            case "usage":
                return await RunUsageSessionAsync(args.Skip(1).ToArray());
            case "revert":
                return await RunRevertSessionAsync(logger, args.Skip(1).ToArray());
            case "checkpoint":
                return await RunCheckpointSessionAsync(logger, args.Skip(1).ToArray());
            case "checkpoints":
                return await RunListCheckpointsAsync(logger, args.Skip(1).ToArray());
            case "rewind":
                return await RunRewindSessionAsync(logger, args.Skip(1).ToArray());
            case "tree":
                return await RunTreeSessionsAsync();
            case "fork":
                return await RunForkSessionAsync(logger, args.Skip(1).ToArray());
            case "maintenance":
                return await SessionMaintenanceVerb.RunAsync(logger, args.Skip(1).ToArray(), CancellationToken.None).ConfigureAwait(false);
            default:
                Console.Error.WriteLine("""
                                        Usage: harbor sessions [list|rename|retitle|export|export-zip|export-md|export-json|import|search|usage|revert|checkpoint|checkpoints|rewind|tree|fork|maintenance]
                                          sessions                        list all sessions
                                          sessions rename <id> <title>    rename a session
                                          sessions retitle <id>           regenerate a title from the first user message
                                          sessions export <id> [file]     export session to a portable file (default: harbor-session-<id>.jsonl)
                                          sessions export-zip <id> [file] export session as ONE zip archive: log + attachments + manifest
                                                                  (default: harbor-session-<id>.zip)
                                          sessions export-md <id> [file]  export session as a Markdown document (default: harbor-session-<id>.md)
                                          sessions export-json <id> [file] export session as a JSON document (default: harbor-session-<id>.json)
                                          sessions import <file>          import an exported file as a NEW session
                                          sessions import [--from claude|codex] <file>
                                                                          import a Claude Code / Codex JSONL history as a NEW session
                                          sessions search <query> [--session <id>]   find messages by substring
                                          sessions usage <id>                        report session cost and token usage
                                          sessions revert <id> <message-id>          rewind session to the given message
                                          sessions checkpoint <id> <message-id> [label]  mark a checkpoint over the message
                                          sessions checkpoints <id>             list checkpoints (and rewind trail) of the session
                                          sessions rewind <id> <checkpoint-id>  rewind session to the checkpoint (records a trail marker)
                                          sessions tree                   show fork/branch lineage as an indented tree
                                          sessions fork <id> <message-id>            branch a NEW session copying messages up to and including the given one
                                          sessions maintenance [--older-than-days N] [--keep-recent N] [--apply]
                                                                          plan stale-session cleanup; dry run unless --apply is passed
                                        """);
                return 2;
        }
    }

    internal static async Task<int> RunListSessionsAsync(ILogger logger)
    {
        SessionsVerbLog.ListingSessions(logger);
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var result = await store.ListAsync().ConfigureAwait(false);
        if (result.IsFailure)
        {
            // #603: the same swallow as the `/sessions` slash command, and worse
            // — it returned 0, so `harbor sessions list` exited SUCCESS on an
            // unreadable store after printing nothing. A script wrapping it
            // would read that as "no sessions", which is a different and much
            // more expensive conclusion than "the store is broken".
            SessionsVerbLog.CannotListSessions(logger, result.Error);
            Console.Error.WriteLine($"Cannot list sessions: {result.Error}");
            return 1;
        }

        SessionsVerbLog.FoundSessions(logger, result.Value.Count);
        foreach (var s in result.Value)
            Console.WriteLine($"  {s.Id} — {s.Title} [{s.ProviderId}/{s.Model}]");
        return 0;
    }

    internal static async Task<int> RunTreeSessionsAsync()
    {
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        return await SessionTreeRunner.RunAsync(Console.Out, Console.Error, store).ConfigureAwait(false);
    }

    internal static async Task<int> RunRenameSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: harbor sessions rename <session-id> <new-title>");
            return 2;
        }

        string sessionId = args[0];
        string title = string.Join(' ', args.Skip(1));
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var loaded = await store.GetAsync(sessionId).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            SessionsVerbLog.RenameFailed(logger, loaded.Error);
            Console.Error.WriteLine($"Cannot rename '{sessionId}': {loaded.Error}");
            return 1;
        }

        var renamed = loaded.Value with { Title = title, UpdatedAt = DateTimeOffset.UtcNow };
        var saved = await store.UpdateAsync(renamed).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            SessionsVerbLog.RenamePersistFailed(logger, saved.Error);
            Console.Error.WriteLine($"Cannot persist rename: {saved.Error}");
            return 1;
        }

        Console.WriteLine($"Renamed {sessionId} → \"{title}\"");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions retitle &lt;session-id&gt;</c> (issue #2684 S06):
    ///     one auxiliary naming request, no agent loop, title-only update.
    /// </summary>
    internal static async Task<int> RunRetitleSessionAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions retitle <session-id>");
            return 2;
        }

        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var providers = host.Services.GetRequiredService<IProviderRegistry>();
        return await SessionRetitleRunner.RunAsync(Console.Out, Console.Error, store, providers, args[0])
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunExportSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions export <session-id> [file]");
            return 2;
        }

        string sessionId = args[0];
        string path = args.Length > 1 ? args[1] : $"harbor-session-{sessionId}.jsonl";
        using var host = HostBuilder.Build();
        var porter = host.Services.GetRequiredService<ISessionPorter>();
        await using var output = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);

        var exported = await porter.ExportAsync(host.Services.GetRequiredService<ISessionStore>(), sessionId, output)
            .ConfigureAwait(false);
        if (exported.IsFailure)
        {
            SessionsVerbLog.ExportFailed(logger, exported.Error);
            Console.Error.WriteLine($"Export failed: {exported.Error}");
            return 1;
        }

        Console.WriteLine($"Exported session {sessionId} → {path}");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions export-zip &lt;session-id&gt; [file]</c> —
    ///     write the session as ONE zip archive: the canonical log (every
    ///     record, shadowed ones included), the attachments, and the manifest
    ///     that says which format wrote them (#1750). The archive is the
    ///     artifact a user attaches to a bug report, so it never carries a
    ///     credential — whatever tier the log on disk was written in.
    /// </summary>
    internal static async Task<int> RunExportZipSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions export-zip <session-id> [file]");
            return 2;
        }

        string sessionId = args[0];
        string path = args.Length > 1 ? args[1] : $"harbor-session-{sessionId}.zip";
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var writer = host.Services.GetRequiredService<SessionArchiveWriter>();

        int code = await SessionExportZipRunner
            .RunAsync(Console.Out, Console.Error, store, writer, sessionId, path)
            .ConfigureAwait(false);
        if (code != 0)
        {
            SessionsVerbLog.ExportZipFailed(logger, sessionId);
        }

        return code;
    }

    /// <summary>
    ///     <c>harbor sessions export-md|export-json &lt;session-id&gt; [file]</c> —
    ///     render the session's metadata and current message surface as a single
    ///     human-readable document. The core lives in
    ///     <see cref="SessionExportFormattedRunner" /> (read-only, #2675 item 049).
    /// </summary>
    internal static async Task<int> RunExportFormattedSessionAsync(
        ILogger logger,
        string[] args,
        SessionExportFormat format)
    {
        string verb = format == SessionExportFormat.Markdown ? "export-md" : "export-json";
        string extension = format == SessionExportFormat.Markdown ? "md" : "json";
        if (args.Length < 1)
        {
            Console.Error.WriteLine($"Usage: harbor sessions {verb} <session-id> [file]");
            return 2;
        }

        string sessionId = args[0];
        string path = args.Length > 1 ? args[1] : $"harbor-session-{sessionId}.{extension}";
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        int code = await SessionExportFormattedRunner
            .RunAsync(Console.Out, Console.Error, store, sessionId, path, format)
            .ConfigureAwait(false);
        if (code != 0)
        {
            SessionsVerbLog.ExportFailed(logger, $"{verb} for {sessionId}");
        }

        return code;
    }

    internal static async Task<int> RunImportSessionAsync(ILogger logger, string[] args)
    {
        string? source = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (token == "--from" && i + 1 < args.Length)
            {
                source = args[++i];
                continue;
            }

            if (token.StartsWith('-'))
            {
                Console.Error.WriteLine($"harbor sessions import: unknown option '{token}'.");
                return 2;
            }

            positional.Add(token);
        }

        if (positional.Count < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions import [--from claude|codex] <file>");
            return 2;
        }

        string path = positional[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        if (source is not null)
        {
            if (source != ForeignSessionImport.Claude && source != ForeignSessionImport.Codex)
            {
                Console.Error.WriteLine("harbor sessions import: --from must be 'claude' or 'codex'.");
                return 2;
            }

            return await RunImportForeignAsync(logger, source, path).ConfigureAwait(false);
        }

        using var host = HostBuilder.Build();
        var porter = host.Services.GetRequiredService<ISessionPorter>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        var imported = await porter.ImportAsync(host.Services.GetRequiredService<ISessionStore>(), reader)
            .ConfigureAwait(false);
        if (imported.IsFailure)
        {
            SessionsVerbLog.ImportFailed(logger, imported.Error);
            Console.Error.WriteLine($"Import failed: {imported.Error}");
            return 1;
        }

        string newId = imported.Value;
        var created = await host.Services.GetRequiredService<ISessionStore>().GetAsync(newId).ConfigureAwait(false);
        string title = created.IsSuccess ? created.Value.Title : "?";
        Console.WriteLine($"Imported {Path.GetFileName(path)} → new session {newId} \"{title}\"");
        return 0;
    }

    /// <summary>
    ///     Create a fresh Harbor session and fill it with the conversation
    ///     projected from a Claude Code or Codex JSONL file (#2674 F13).
    /// </summary>
    private static async Task<int> RunImportForeignAsync(ILogger logger, string source, string path)
    {
        string jsonl;
        try
        {
            jsonl = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            SessionsVerbLog.ImportFailed(logger, ex.Message);
            Console.Error.WriteLine($"import: cannot read '{path}': {ex.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            SessionsVerbLog.ImportFailed(logger, ex.Message);
            Console.Error.WriteLine($"import: cannot read '{path}': {ex.Message}");
            return 1;
        }

        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        // An imported session is a read-only transcript of another agent: the
        // source name travels as the model id and a neutral id as the provider.
        var created = await store.CreateAsync(Environment.CurrentDirectory, "code", "imported", source)
            .ConfigureAwait(false);
        if (created.IsFailure)
        {
            SessionsVerbLog.ImportFailed(logger, created.Error);
            Console.Error.WriteLine($"import: session creation failed: {created.Error}");
            return 1;
        }

        string sessionId = created.Value.Id;
        Result<ForeignSessionImport.ForeignImportResult> parsed =
            ForeignSessionImport.Parse(source, jsonl, sessionId);
        if (parsed.IsFailure)
        {
            SessionsVerbLog.ImportFailed(logger, parsed.Error);
            Console.Error.WriteLine($"import: {parsed.Error}");
            return 1;
        }

        if (parsed.Value.Messages.Count == 0)
        {
            Console.Error.WriteLine($"import: no conversation found in '{path}' for source '{source}'.");
            return 1;
        }

        Result appended = await store.AppendMessagesBatchAsync(sessionId, parsed.Value.Messages)
            .ConfigureAwait(false);
        if (appended.IsFailure)
        {
            SessionsVerbLog.ImportFailed(logger, appended.Error);
            Console.Error.WriteLine($"import: failed to store messages: {appended.Error}");
            return 1;
        }

        Console.WriteLine(
            $"Imported {parsed.Value.Imported} message(s) from {source} '{Path.GetFileName(path)}' " +
            $"→ new session {sessionId} ({parsed.Value.Skipped} line(s) skipped).");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions search &lt;query&gt; [--session &lt;id&gt;]</c> —
    ///     case-insensitive substring scan over persisted messages; the core
    ///     lives in <see cref="SessionSearchRunner" /> (read-only).
    /// </summary>
    internal static async Task<int> RunSearchSessionsAsync(string[] args)
    {
        string? sessionFilter = null;
        List<string> rest = [];
        int i = 0;
        while (i < args.Length)
        {
            if (args[i] is "--session" or "-s" && i + 1 < args.Length)
            {
                sessionFilter = args[i + 1];
                i += 2;
                continue;
            }

            rest.Add(args[i]);
            i++;
        }

        if (rest.Count == 0)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions search <query> [--session <id>]
                                      Search all persisted messages (case-insensitive substring).
                                    """);
            return 2;
        }

        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        return await SessionSearchRunner.RunAsync(Console.Out, Console.Error, store, string.Join(' ', rest), sessionFilter)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     <c>harbor sessions usage &lt;session-id&gt;</c> — per-session cost and
    ///     token usage from <see cref="ISessionStore.GetStatsAsync" />; the core
    ///     lives in <see cref="SessionUsageRunner" /> (read-only).
    /// </summary>
    internal static async Task<int> RunUsageSessionAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions usage <session-id>");
            return 2;
        }

        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        return await SessionUsageRunner.RunAsync(Console.Out, Console.Error, store, args[0])
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     <c>harbor sessions revert &lt;session-id&gt; &lt;message-id&gt;</c> —
    ///     rewind the session to the given message: it and everything before it
    ///     stays, everything after is deleted (backend-level "rewind to here").
    /// </summary>
    internal static async Task<int> RunRevertSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions revert <session-id> <message-id>
                                      Rewind to the given message: every message AFTER it leaves the
                                      conversation (the target itself is kept).
                                    """);
            return 2;
        }

        string sessionId = args[0];
        string messageId = args[1];
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var reverted = await store.DeleteMessagesAfterAsync(sessionId, messageId).ConfigureAwait(false);
        if (reverted.IsFailure)
        {
            SessionsVerbLog.RevertFailed(logger, reverted.Error);
            Console.Error.WriteLine($"Cannot revert '{sessionId}' to '{messageId}': {reverted.Error}");
            return 1;
        }

        var messages = await store.GetMessagesAsync(sessionId).ConfigureAwait(false);
        int remaining = messages.IsSuccess ? messages.Value.Count : -1;
        // #1752: the rewind moves the tail out of the conversation; it does
        // not delete anything, so the sentence must not say it did.
        Console.WriteLine($"Reverted session {sessionId}: {reverted.Value} message(s) left the conversation, {remaining} remain.");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions checkpoint &lt;session-id&gt; &lt;message-id&gt; [label]</c> —
    ///     mark a checkpoint over the message (records its message index).
    ///     Jsonl backend only in slice 1 (#1247).
    /// </summary>
    internal static async Task<int> RunCheckpointSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions checkpoint <session-id> <message-id> [label]
                                      Record a checkpoint over the given message.
                                    """);
            return 2;
        }

        string sessionId = args[0];
        string messageId = args[1];
        string? label = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var marked = await new SessionCheckpointRunner(store).CheckpointAsync(sessionId, messageId, label).ConfigureAwait(false);
        if (marked.IsFailure)
        {
            SessionsVerbLog.CheckpointFailed(logger, marked.Error);
            Console.Error.WriteLine($"Cannot checkpoint '{sessionId}' at '{messageId}': {marked.Error}");
            return 1;
        }

        Console.WriteLine($"Checkpoint {marked.Value.Id} on session {sessionId}: message '{messageId}' at index {marked.Value.MessageIndex}.");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions checkpoints &lt;session-id&gt;</c> — list the
    ///     session's checkpoints and rewind trail markers in file order.
    ///     Jsonl backend only in slice 1 (#1247).
    /// </summary>
    internal static async Task<int> RunListCheckpointsAsync(ILogger logger, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions checkpoints <session-id>
                                      List checkpoints (and rewind trail markers) of the session.
                                    """);
            return 2;
        }

        string sessionId = args[0];
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var listed = await new SessionCheckpointRunner(store).ListAsync(sessionId).ConfigureAwait(false);
        if (listed.IsFailure)
        {
            SessionsVerbLog.CannotListCheckpoints(logger, listed.Error);
            Console.Error.WriteLine($"Cannot list checkpoints of '{sessionId}': {listed.Error}");
            return 1;
        }

        if (listed.Value.Count == 0)
            Console.WriteLine($"No checkpoints on session {sessionId}.");
        foreach (var c in listed.Value)
        {
            string line = c.IsRewindTrail
                ? $"  {c.Id} — rewind to '{c.MessageId}' (removed {c.Removed}, {c.CreatedAt:yyyy-MM-dd HH:mm})"
                : $"  {c.Id} — '{c.MessageId}' at index {c.MessageIndex} ({c.CreatedAt:yyyy-MM-dd HH:mm}){(c.Label is null ? string.Empty : $" — {c.Label}")}";
            Console.WriteLine(line);
        }

        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions rewind &lt;session-id&gt; &lt;checkpoint-id&gt;</c> —
    ///     truncate the session to the checkpoint's anchor message and record
    ///     a trail marker (the honest trail — the rewind itself stays in the
    ///     file). Jsonl backend only in slice 1 (#1247).
    /// </summary>
    internal static async Task<int> RunRewindSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions rewind <session-id> <checkpoint-id>
                                      Truncate the session to the checkpoint's message and record a trail marker.
                                    """);
            return 2;
        }

        string sessionId = args[0];
        string checkpointId = args[1];
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();

        var rewound = await new SessionCheckpointRunner(store).RewindAsync(sessionId, checkpointId).ConfigureAwait(false);
        if (rewound.IsFailure)
        {
            SessionsVerbLog.RewindFailed(logger, rewound.Error);
            Console.Error.WriteLine($"Cannot rewind '{sessionId}' to '{checkpointId}': {rewound.Error}");
            return 1;
        }

        Console.WriteLine($"Rewound session {sessionId} to checkpoint {checkpointId}: {rewound.Value.Removed} message(s) left the conversation, {rewound.Value.Remaining} remain.");
        return 0;
    }

    /// <summary>
    ///     <c>harbor sessions fork &lt;session-id&gt; &lt;message-id&gt;</c> —
    ///     branch a NEW session that copies every message up to and including the
    ///     given one (the source session is left untouched). The fork records its
    ///     lineage via <c>ParentSessionId</c> and a "(fork)" title suffix.
    /// </summary>
    internal static async Task<int> RunForkSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("""
                                    Usage: harbor sessions fork <session-id> <message-id>
                                      Create a NEW session with all messages up to AND INCLUDING the given one.
                                    """);
            return 2;
        }

        string sessionId = args[0];
        string messageId = args[1];
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        var runner = new SessionForkRunner(store);

        var forked = await runner.ForkAsync(sessionId, messageId).ConfigureAwait(false);
        if (forked.IsFailure)
        {
            SessionsVerbLog.ForkFailed(logger, forked.Error);
            Console.Error.WriteLine(forked.Error);
            return 1;
        }

        Console.WriteLine($"Forked session {sessionId} → {forked.Value.ForkId}: copied {forked.Value.Copied} message(s) up to '{messageId}'.");
        return 0;
    }
}

/// <summary>
///     <c>harbor sessions maintenance</c> (#2686 K05): plan stale-session
///     cleanup from a retention policy. Parsing and planning are pure and live
///     in <see cref="SessionRetentionPlanner"/>; only the store access and the
///     optional deletion touch IO. The default is a dry run, so a typo cannot
///     delete sessions.
/// </summary>
internal static class SessionMaintenanceVerb
{
    /// <summary>Run the maintenance verb with an already-parsed flag tail.</summary>
    /// <param name="logger">Diagnostic logger.</param>
    /// <param name="args">Arguments after <c>maintenance</c>.</param>
    /// <param name="cancellationToken">Cancellation token for store calls.</param>
    /// <returns>0 on success, 1 on store failure and 2 on bad arguments.</returns>
    internal static async Task<int> RunAsync(
        ILogger logger,
        string[] args,
        CancellationToken cancellationToken)
    {
        Result<SessionMaintenanceArgs> parsed = SessionRetentionPlanner.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        SessionMaintenanceArgs request = parsed.Value;
        using var host = HostBuilder.Build();
        var store = host.Services.GetRequiredService<ISessionStore>();
        Result<IReadOnlyList<Session>> listed = await store
            .ListAsync(ct: cancellationToken)
            .ConfigureAwait(false);
        if (listed.IsFailure)
        {
            SessionsVerbLog.MaintenanceListFailed(logger, listed.Error);
            Console.Error.WriteLine($"sessions maintenance: cannot list sessions: {listed.Error}");
            return 1;
        }

        Result<SessionRetentionPlan> planned = SessionRetentionPlanner.Build(
            listed.Value, request.Options, DateTimeOffset.UtcNow);
        if (planned.IsFailure)
        {
            Console.Error.WriteLine(planned.Error);
            return 2;
        }

        SessionRetentionPlan plan = planned.Value;
        Console.Out.Write(plan.Render());

        if (!request.Apply)
        {
            Console.Out.WriteLine("Dry run: pass --apply to delete the listed sessions.");
            return 0;
        }

        int deleted = 0;
        foreach (SessionRetentionDecision decision in plan.Delete)
        {
            Result result = await store
                .DeleteAsync(decision.Session.Id, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                SessionsVerbLog.MaintenanceDeleteFailed(logger, decision.Session.Id, result.Error);
                Console.Error.WriteLine(
                    $"sessions maintenance: cannot delete {decision.Session.Id}: {result.Error}");
                return 1;
            }
            deleted++;
        }

        Console.Out.WriteLine($"Deleted {deleted.ToString(System.Globalization.CultureInfo.InvariantCulture)} session(s).");
        return 0;
    }
}

/// <summary>
///     SG6: BCL <c>[LoggerMessage]</c> delegates for <see cref="SessionsVerb" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class SessionsVerbLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Listing sessions")]
    public static partial void ListingSessions(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Cannot list sessions: {Error}")]
    public static partial void CannotListSessions(ILogger logger, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Found {Count} sessions")]
    public static partial void FoundSessions(ILogger logger, int count);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Rename failed: {Error}")]
    public static partial void RenameFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Rename persist failed: {Error}")]
    public static partial void RenamePersistFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Export failed: {Error}")]
    public static partial void ExportFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Import failed: {Error}")]
    public static partial void ImportFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Revert failed: {Error}")]
    public static partial void RevertFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Checkpoint failed: {Error}")]
    public static partial void CheckpointFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "Cannot list checkpoints: {Error}")]
    public static partial void CannotListCheckpoints(ILogger logger, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Rewind failed: {Error}")]
    public static partial void RewindFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error, Message = "Fork failed: {Error}")]
    public static partial void ForkFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Zip export of session {SessionId} failed")]
    public static partial void ExportZipFailed(ILogger logger, string sessionId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "sessions maintenance: list failed: {Error}")]
    public static partial void MaintenanceListFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning, Message = "sessions maintenance: delete {SessionId} failed: {Error}")]
    public static partial void MaintenanceDeleteFailed(ILogger logger, string sessionId, string error);
}
