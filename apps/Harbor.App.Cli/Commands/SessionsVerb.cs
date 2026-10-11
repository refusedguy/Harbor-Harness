using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor sessions</c> family: list (default), rename, export, import,
///     search, revert, checkpoint, checkpoints, rewind, tree, fork. Extracted from <c>Program</c> (#176);
///     rename persists a new title via ISessionStore.UpdateAsync,
///     export/import round-trip one session through the portable line payload
///     built by <c>ISessionPorter</c> (see StorageModule for the backend wiring).
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
            case "export":
                return await RunExportSessionAsync(logger, args.Skip(1).ToArray());
            case "import":
                return await RunImportSessionAsync(logger, args.Skip(1).ToArray());
            case "search":
                return await RunSearchSessionsAsync(args.Skip(1).ToArray());
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
            default:
                Console.Error.WriteLine("""
                                        Usage: harbor sessions [list|rename|export|import|search|revert|checkpoint|checkpoints|rewind|tree|fork]
                                          sessions                        list all sessions
                                          sessions rename <id> <title>    rename a session
                                          sessions export <id> [file]     export session to a portable file (default: harbor-session-<id>.jsonl)
                                          sessions import <file>          import an exported file as a NEW session
                                          sessions search <query> [--session <id>]   find messages by substring
                                          sessions revert <id> <message-id>          rewind session to the given message
                                          sessions checkpoint <id> <message-id> [label]  mark a checkpoint over the message
                                          sessions checkpoints <id>             list checkpoints (and rewind trail) of the session
                                          sessions rewind <id> <checkpoint-id>  rewind session to the checkpoint (records a trail marker)
                                          sessions tree                   show fork/branch lineage as an indented tree
                                          sessions fork <id> <message-id>            branch a NEW session copying messages up to and including the given one
                                        """);
                return 2;
        }
    }

    internal static async Task<int> RunListSessionsAsync(ILogger logger)
    {
        logger.LogInformation("Listing sessions");
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
            logger.LogError("Cannot list sessions: {Error}", result.Error);
            Console.Error.WriteLine($"Cannot list sessions: {result.Error}");
            return 1;
        }

        logger.LogInformation("Found {Count} sessions", result.Value.Count);
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
            logger.LogError("Rename failed: {Error}", loaded.Error);
            Console.Error.WriteLine($"Cannot rename '{sessionId}': {loaded.Error}");
            return 1;
        }

        var renamed = loaded.Value with { Title = title, UpdatedAt = DateTimeOffset.UtcNow };
        var saved = await store.UpdateAsync(renamed).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            logger.LogError("Rename persist failed: {Error}", saved.Error);
            Console.Error.WriteLine($"Cannot persist rename: {saved.Error}");
            return 1;
        }

        Console.WriteLine($"Renamed {sessionId} → \"{title}\"");
        return 0;
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
            logger.LogError("Export failed: {Error}", exported.Error);
            Console.Error.WriteLine($"Export failed: {exported.Error}");
            return 1;
        }

        Console.WriteLine($"Exported session {sessionId} → {path}");
        return 0;
    }

    internal static async Task<int> RunImportSessionAsync(ILogger logger, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: harbor sessions import <file>");
            return 2;
        }

        string path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"File not found: {path}");
            return 1;
        }

        using var host = HostBuilder.Build();
        var porter = host.Services.GetRequiredService<ISessionPorter>();
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);

        var imported = await porter.ImportAsync(host.Services.GetRequiredService<ISessionStore>(), reader)
            .ConfigureAwait(false);
        if (imported.IsFailure)
        {
            logger.LogError("Import failed: {Error}", imported.Error);
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
                                      Delete every message AFTER the given one (the target itself is kept).
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
            logger.LogError("Revert failed: {Error}", reverted.Error);
            Console.Error.WriteLine($"Cannot revert '{sessionId}' to '{messageId}': {reverted.Error}");
            return 1;
        }

        var messages = await store.GetMessagesAsync(sessionId).ConfigureAwait(false);
        int remaining = messages.IsSuccess ? messages.Value.Count : -1;
        Console.WriteLine($"Reverted session {sessionId}: deleted {reverted.Value} message(s), {remaining} remain.");
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
            logger.LogError("Checkpoint failed: {Error}", marked.Error);
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
            logger.LogError("Cannot list checkpoints: {Error}", listed.Error);
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
            logger.LogError("Rewind failed: {Error}", rewound.Error);
            Console.Error.WriteLine($"Cannot rewind '{sessionId}' to '{checkpointId}': {rewound.Error}");
            return 1;
        }

        Console.WriteLine($"Rewound session {sessionId} to checkpoint {checkpointId}: deleted {rewound.Value.Removed} message(s), {rewound.Value.Remaining} remain.");
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
            logger.LogError("Fork failed: {Error}", forked.Error);
            Console.Error.WriteLine(forked.Error);
            return 1;
        }

        Console.WriteLine($"Forked session {sessionId} → {forked.Value.ForkId}: copied {forked.Value.Copied} message(s) up to '{messageId}'.");
        return 0;
    }
}
