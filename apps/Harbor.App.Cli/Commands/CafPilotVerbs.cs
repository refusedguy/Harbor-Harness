using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     LB2 pilot (#1235) + CAF-A (#1286) + CAF-B (#1287) + CAF-D (#1289) + CAF-E (#1290) + CAF-C (#1288):
///     ConsoleAppFramework (incremental source generator, AOT-first) on
///     <c>version</c>, <c>storage</c>, <c>sessions</c>, <c>run</c>,
///     <c>providers</c>, <c>models</c>, <c>auth</c>, <c>config</c>,
///     <c>daemon</c>, <c>status</c>, <c>serve</c>, <c>mcp</c>,
///     <c>setup</c>, <c>tui</c>, <c>demo</c>, <c>logs</c>, <c>ask</c>,
///     <c>events</c>, <c>headless</c>, <c>ide</c>, <c>plugin</c>,
///     <c>skill</c> and <c>help</c>. The hand-rolled dispatcher is gone:
///     <c>Program</c> keeps only the no-args/unknown → interactive fallback;
///     Spectre.Console.Cli / System.CommandLine stay forbidden.
/// </summary>
/// <remarks>
///     <para>
///         There is deliberately ONE <c>RunAsync</c> call site in this
///         assembly: the generator keys its emitted file
///         (<c>ConsoleApp.Run.g.cs</c>) without a per-site disambiguator, so
///         two single-command <c>ConsoleApp.RunAsync</c> sites collide with
///         CS8785 (duplicate hintName). One builder app registering all
///         verbs avoids that.
///     </para>
///     <para>
///         <c>Program</c> dispatches the verb first (global
///         <c>--script</c>/<c>--loglevel</c> stripping in
///         <c>Program.Main</c> runs unchanged), then re-attaches the verb so
///         the builder routes it; <c>-h|--help</c> and flag parsing come from
///         the framework. Handlers delegate to <see cref="HelpVerbs" /> — the
///         output text stays byte-for-byte identical to the manual path
///         (pilot handlers cover version/storage; CAF-A adds sessions/run;
///         CAF-B adds providers/models/auth/config; CAF-D adds
///         daemon/status/serve/mcp; CAF-E adds setup/tui/demo — with typed
///         flags, since those verbs ARE flags — and slice 2 adds ask/events/
///         headless/ide/plugin/skill/help; CAF-C adds logs — with typed
///         flags, since that verb IS flags).
///     </para>
///     <para>
///         Exit code: CAF sets <see cref="Environment.ExitCode" /> from the
///         handler's <c>int</c> return (1 on parse/validation failure); the
///         entry point threads that value back to the caller.
///     </para>
/// </remarks>
internal static class CafPilotVerbs
{
    internal static async Task<int> RunAsync(string verb, string[] rest)
    {
        var app = ConsoleApp.Create();
        app.Add("version", CafPilotCommands.Version);
        app.Add("storage", CafPilotCommands.Storage);
        app.Add("sessions", CafSessionCommands.Sessions);
        app.Add("run", CafRunCommands.Run);
        app.Add("providers", CafProviderCommands.Providers);
        app.Add("models", CafProviderCommands.Models);
        app.Add("auth", CafAuthCommands.Auth);
        app.Add("config", CafConfigCommands.Config);
        app.Add("daemon", CafDaemonCommands.Daemon);
#if HARBOR_WITH_DAEMON
        app.Add("status", CafStatusCommands.Status);
#endif
        app.Add("serve", CafServeCommands.Serve);
        app.Add("mcp", CafMcpCommands.Mcp);
        app.Add("setup", CafSetupCommands.Setup);
        app.Add("tui", CafTuiCommands.Tui);
        app.Add("demo", CafDemoCommands.Demo);
        app.Add("logs", CafLogsCommands.Logs);
        app.Add("ask", CafAskCommands.Ask);
        app.Add("events", CafEventsCommands.Events);
        app.Add("headless", CafHeadlessCommands.Headless);
#if HARBOR_WITH_DAEMON
        app.Add("ide", CafIdeCommands.Ide);
#endif
        app.Add("plugin", CafPluginCommands.Plugin);
        app.Add("skill", CafSkillCommands.Skill);
        app.Add("help", CafHelpCommands.Help);
        await app.RunAsync([verb, ..rest]).ConfigureAwait(false);
        return Environment.ExitCode;
    }
}

/// <summary>
///     CAF command handlers for the LB2 pilot. The XML doc comments become
///     the <c>--help</c> text (CAF reads documentation, not attributes).
/// </summary>
internal static class CafPilotCommands
{
    /// <summary>Print the Harbor version and the .NET runtime version.</summary>
    public static int Version() => HelpVerbs.PrintVersion();

    /// <summary>Print the available session storage backends.</summary>
    public static int Storage() => HelpVerbs.PrintStorageOptions();
}
