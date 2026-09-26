using Harbor.Ipc;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Options for <c>harbor tui</c> attach mode.
/// </summary>
/// <param name="SessionId">Bind the agent loop to this session before streaming; null = observe only.</param>
/// <param name="SinceSequence">
///     Replay envelopes newer than this server sequence before going live
///     (the daemon's replay ring); null = live from now.
/// </param>
/// <param name="ShowHelp">Print usage instead of attaching.</param>
public sealed record TuiAttachOptions(string? SessionId, ulong? SinceSequence, bool ShowHelp)
{
    /// <summary>
    ///     Parse tui-attach args.
    /// </summary>
    /// <param name="args">Args after the <c>tui</c> keyword.</param>
    /// <param name="options">The parsed options (null on failure).</param>
    /// <param name="error">Human-readable error on failure.</param>
    /// <returns>True when parsing succeeded.</returns>
    public static bool TryParse(string[] args, out TuiAttachOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? sessionId = null;
        ulong? since = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--help" or "-h")
            {
                options = new TuiAttachOptions(null, null, true);
                error = null;
                return true;
            }

            if (a is "--session" or "-s")
            {
                if (i + 1 >= args.Length)
                {
                    options = null;
                    error = "Missing value for --session <id>.";
                    return false;
                }

                sessionId = args[i + 1];
                i++;
                continue;
            }

            if (a.StartsWith("--session=", StringComparison.OrdinalIgnoreCase))
            {
                sessionId = a["--session=".Length..];
                continue;
            }

            if (a == "--since")
            {
                if (i + 1 >= args.Length)
                {
                    options = null;
                    error = "Missing value for --since <sequence>.";
                    return false;
                }

                if (!ulong.TryParse(args[i + 1], out ulong parsed))
                {
                    options = null;
                    error = $"Invalid --since value '{args[i + 1]}': expected a non-negative sequence number.";
                    return false;
                }

                since = parsed;
                i++;
                continue;
            }

            if (a.StartsWith("--since=", StringComparison.OrdinalIgnoreCase))
            {
                if (!ulong.TryParse(a["--since=".Length..], out ulong parsed))
                {
                    options = null;
                    error = $"Invalid --since value '{a}': expected a non-negative sequence number.";
                    return false;
                }

                since = parsed;
                continue;
            }

            if (a.StartsWith("-", StringComparison.Ordinal))
            {
                options = null;
                error = $"Unknown flag: {a}";
                return false;
            }

            options = null;
            error = $"Unexpected argument: {a}";
            return false;
        }

        options = new TuiAttachOptions(sessionId, since, false);
        error = null;
        return true;
    }

    /// <summary>Print tui-attach usage.</summary>
    /// <param name="output">Where usage goes.</param>
    public static void PrintUsage(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("""
                         harbor tui — attach to a live daemon and stream its events.

                         Usage:
                           harbor tui [--session <id>] [--since <seq>] [--help]

                           --session <id>   bind the agent loop to the session first
                           --since <seq>    replay missed envelopes after <seq>, then live
                           --help           show this help

                         Event JSON goes to stdout (one object per line); status
                         notices go to stderr. Ctrl-C detaches (the daemon keeps
                         running). Start one with `harbor serve` first.
                         """);
    }
}

/// <summary>
///     <c>harbor tui</c> attach mode: handshake with the live daemon
///     (<c>ConnectAsync</c> sends the protocol-version handshake), optionally
///     bind a session, then print the event stream as JSON lines — replaying
///     anything missed after <c>--since</c> before going live.
/// </summary>
public static class TuiAttachRunner
{
    /// <summary>
    ///     Attach and stream until Ctrl-C.
    /// </summary>
    /// <param name="output">Where event JSON lines go (stdout).</param>
    /// <param name="error">Where status notices go (stderr).</param>
    /// <param name="client">Connected-able Harbor client (ipc-client mode).</param>
    /// <param name="options">Parsed attach options.</param>
    /// <param name="ct">Cancellation token (Ctrl-C).</param>
    /// <returns>Process exit code (0 = clean detach, 1 = failure).</returns>
    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        IHarborClient client,
        TuiAttachOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ShowHelp)
        {
            TuiAttachOptions.PrintUsage(output);
            return 0;
        }

        await client.ConnectAsync(ct).ConfigureAwait(false);
        if (!client.IsConnected)
        {
            error.WriteLine("tui: unable to connect to the Harbor daemon (is one running? start it with `harbor serve` or `harbor daemon start`).");
            return 1;
        }

        string? sessionId = options.SessionId;
        if (sessionId is not null)
        {
            var sessionResult = await client.GetSessionAsync(sessionId, ct).ConfigureAwait(false);
            if (sessionResult.IsFailure)
            {
                error.WriteLine($"tui: session '{sessionId}' not found: {sessionResult.Error}");
                return 1;
            }

            var bind = await client.StartAgentAsync(sessionId, sessionResult.Value.Agent, ct).ConfigureAwait(false);
            if (bind.IsFailure)
            {
                error.WriteLine($"tui: failed to bind agent to session '{sessionId}': {bind.Error}");
                return 1;
            }

            error.WriteLine($"attached to session '{sessionId}' (agent {sessionResult.Value.Agent})");
        }
        else
        {
            error.WriteLine("attached to daemon");
        }

        if (options.SinceSequence.HasValue)
            error.WriteLine($"replaying events after sequence {options.SinceSequence.Value}, then live…");
        else
            error.WriteLine("streaming live events (Ctrl-C to detach)…");

        await HarborEventJson.WriteEventLinesAsync(
            client.SubscribeToEventsAsync(ct, options.SinceSequence),
            output,
            maxEvents: null,
            ct).ConfigureAwait(false);
        return 0;
    }
}
