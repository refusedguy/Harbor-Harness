using Harbor.Ipc;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Options for <c>harbor events --watch</c>.
/// </summary>
/// <param name="Watch">Stream live events; false = print usage.</param>
/// <param name="Count">Stop after this many events; null = endless.</param>
/// <param name="ShowHelp">Print usage instead of watching.</param>
public sealed record EventsWatchOptions(bool Watch, int? Count, bool ShowHelp)
{
    /// <summary>
    ///     Parse events args.
    /// </summary>
    /// <param name="args">Args after the <c>events</c> keyword.</param>
    /// <param name="options">The parsed options (null on failure).</param>
    /// <param name="error">Human-readable error on failure.</param>
    /// <returns>True when parsing succeeded.</returns>
    public static bool TryParse(string[] args, out EventsWatchOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool watch = false;
        int? count = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--help" or "-h")
            {
                options = new EventsWatchOptions(false, null, true);
                error = null;
                return true;
            }

            if (a is "--watch" or "-w")
            {
                watch = true;
                continue;
            }

            if (a is "--count" or "-n")
            {
                if (i + 1 >= args.Length)
                {
                    options = null;
                    error = "Missing value for --count <N>.";
                    return false;
                }

                if (!int.TryParse(args[i + 1], out int parsed) || parsed <= 0)
                {
                    options = null;
                    error = $"Invalid --count value '{args[i + 1]}': expected a positive integer.";
                    return false;
                }

                count = parsed;
                i++;
                continue;
            }

            if (a.StartsWith("--count=", StringComparison.OrdinalIgnoreCase))
            {
                string raw = a["--count=".Length..];
                if (!int.TryParse(raw, out int parsed) || parsed <= 0)
                {
                    options = null;
                    error = $"Invalid --count value '{a}': expected a positive integer.";
                    return false;
                }

                count = parsed;
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

        options = new EventsWatchOptions(watch, count, false);
        error = null;
        return true;
    }

    /// <summary>Print events usage.</summary>
    /// <param name="output">Where usage goes.</param>
    public static void PrintUsage(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("""
                         harbor events — inspect the daemon's event stream.

                         Usage:
                           harbor events --watch [--count <N>] [--help]

                           --watch      connect and print events as JSON lines
                           --count <N>  stop after N events (default: endless)
                           --help       show this help

                         Debug-surface over the existing IPC socket (MessagePack
                         wire untouched): event JSON goes to stdout, one object
                         per line — pipe it to jq. Status notices go to stderr.
                         The daemon must be running (`harbor serve`).
                         """);
    }
}

/// <summary>
///     <c>harbor events --watch</c>: connect to the daemon socket and print
///     its event stream as human-readable JSON lines on stdout.
/// </summary>
public static class EventsWatchRunner
{
    /// <summary>
    ///     Watch the daemon's events until Ctrl-C or <c>--count</c>.
    /// </summary>
    /// <param name="output">Where event JSON lines go (stdout).</param>
    /// <param name="error">Where status notices go (stderr).</param>
    /// <param name="client">Connected-able Harbor client (ipc-client mode).</param>
    /// <param name="options">Parsed watch options.</param>
    /// <param name="ct">Cancellation token (Ctrl-C).</param>
    /// <returns>Process exit code (0 = ok, 1 = failure).</returns>
    public static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        IHarborClient client,
        EventsWatchOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ShowHelp || !options.Watch)
        {
            EventsWatchOptions.PrintUsage(output);
            return 0;
        }

        await client.ConnectAsync(ct).ConfigureAwait(false);
        if (!client.IsConnected)
        {
            error.WriteLine("events: unable to connect to the Harbor daemon (is one running? start it with `harbor serve` or `harbor daemon start`).");
            return 1;
        }

        error.WriteLine(options.Count.HasValue
            ? $"watching daemon events (first {options.Count.Value})…"
            : "watching daemon events (Ctrl-C to stop)…");
        int written = await HarborEventJson.WriteEventLinesAsync(
            client.SubscribeToEventsAsync(ct),
            output,
            options.Count,
            ct).ConfigureAwait(false);
        error.WriteLine($"stopped after {written} event(s).");
        return 0;
    }
}
