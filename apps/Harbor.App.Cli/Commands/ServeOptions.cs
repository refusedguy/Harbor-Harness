namespace Harbor.App.Cli.Commands;

/// <summary>
///     Options for <c>harbor serve</c> — a foreground alias over the headless
///     daemon (<c>harbor --headless</c> / <c>harbor daemon start</c> without the
///     detach): full agent host + IPC server, blocking until SIGINT/SIGTERM.
/// </summary>
/// <param name="ShowHelp">Print usage instead of starting.</param>
public sealed record ServeOptions(bool ShowHelp)
{
    /// <summary>
    ///     Parse serve args. Unknown args are passed through to the host
    ///     builder untouched — only <c>--help</c>/<c>-h</c> is interpreted here.
    /// </summary>
    /// <param name="args">Args after the <c>serve</c> keyword.</param>
    /// <returns>The parsed options.</returns>
    public static ServeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool help = args.Any(a => a is "--help" or "-h");
        return new ServeOptions(help);
    }

    /// <summary>Print serve usage.</summary>
    /// <param name="output">Where usage goes.</param>
    public static void PrintUsage(TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.WriteLine("""
                         harbor serve — run the Harbor daemon in the foreground.

                         Usage:
                           harbor serve [--help]

                         Starts the full agent host (EventBus, tools, providers,
                         storage) plus the IPC server — no UI, no REPL — and blocks
                         until SIGINT/SIGTERM. Remote and local clients attach over
                         IPC. Same listener as `harbor daemon start`, but attached
                         to this terminal (useful for systemd/docker/ssh).

                         Environment:
                           HARBOR_MODE      forced to ipc-server when unset
                           HARBOR_IPC_PIPE  socket/pipe name (default harbor-ipc)
                           HARBOR_LISTEN    loopback | tailscale0 | all (optional TCP)
                           HARBOR_PORT      TCP port (default 48710)
                         """);
    }
}
